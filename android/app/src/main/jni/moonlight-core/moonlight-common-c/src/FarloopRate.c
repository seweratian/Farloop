// Farloop 码率自适应（控制端）
//
// 思路照 airhand（MIT，crates/airhand-link/src/flow.rs）的码率控制：
//   每一帧算它在网络里排了多久的队 = 单向时延 - 最近 10 秒里最小的单向时延。
//   单向时延 = 第一个包到达的时刻（本机时钟）-（采集时刻 + 被控端处理耗时）（被控端时钟），
//   两边时钟差是个常数，减掉最小值就消掉了。
//   - 排队超过 30 ms：降码率（到量出来的带宽的 85%，或者当前的 90%），降完等一个往返再看
//   - 排队少于 10 ms、编码器确实用到了一半以上的码率、最近没怎么丢帧：慢慢往上加（每秒 +15%，接近上次拥塞时的带宽时 +4%）
//   - 丢帧率超过 6%：按丢帧率降；超过 2%：不再加
//   最高不超过画质设置里的码率，最低到它的 1/6。
// 新码率经控制通道发给被控端（Farloop 改过的 Sunshine，消息类型 0x55F0，载荷 uint32 小端 kbps），
// 编码器不出关键帧、不重置，直接按新码率编码。
#include "Limelight-internal.h"

#define FR_MIN_OWD_WINDOW_MS 10000
// 0.20：30 / 10 是 airhand 按有线 / Wi-Fi 定的；5G 下 40~55 ms 的抖动很常见，按 30 算会把码率一路砍到底（实测降到 1.3M）
#define FR_QUEUE_HIGH_MS 60.0
#define FR_QUEUE_LOW_MS 20.0
#define FR_STANDING 3
#define FR_RATE_SPAN_MS 1000
#define FR_MIN_RATE_SPAN_MS 100
#define FR_LOSS_HIGH 0.06
#define FR_LOSS_LOW 0.02
#define FR_LOSS_CUT_EVERY_MS 1000
#define FR_LOSS_TTL_MS 3000
#define FR_SAMPLES 128
#define FR_MINS 64

typedef struct { double owd; uint64_t at; } FR_MIN;
typedef struct { uint64_t arrived; uint64_t before; double queue; } FR_SAMPLE;

static bool frEnabled;          // Java 打开（被控端支持时）
static volatile int frCapKbps;    // 画质档位的码率上限（0 = 用开始串流时的码率）；串流中可以改，立即生效
static volatile int frPendingFps;  // 等着发给被控端的帧率（0 = 没有）
static volatile int frPendingRes;  // 等着发给被控端的分辨率 (宽 << 16) | 高（0 = 没有）
static int frTargetKbps;        // 上限 = 开始串流时的码率
static double frEstimate;
static int frApplied;
static volatile int frPendingKbps;  // 等着发给被控端的
static volatile int frSentKbps;

static FR_MIN frMins[FR_MINS];
static int frMinCount;
static FR_SAMPLE frSamples[FR_SAMPLES];
static int frSampleHead, frSampleCount;
static uint64_t frBytes;

static uint64_t frRaised;       // 0 = 没有
static uint64_t frHold;
static int frEpisodeTimes;      // -1 = 不在排队中
static double frEpisodeLast;
static double frCapacity;       // 0 = 没有
static double frLoss;
static uint64_t frLossAt, frLossCut;
static uint32_t frGoodFrames, frLostFrames;
static uint64_t frLossWindowStart;
static volatile int frLastQueueMs;

static double frFloor(void) {
    int f = frTargetKbps / 6;
    if (f < 300) f = 300;
    if (f > frTargetKbps) f = frTargetKbps;
    return f;
}

static void frApply(void) {
    int e = (int)(frEstimate + 0.5);
    if (e < frApplied || e >= frApplied + frApplied / 20 || e == frTargetKbps) {
        if (e != frApplied) {
            Limelog("Farloop: bitrate %d -> %d kbps (queue %d ms, loss %.1f%%)\n", frApplied, e, frLastQueueMs, frLoss * 100);
        }
        frApplied = e;
        frPendingKbps = e;
    }
}

static void frReset(void) {
    frMinCount = 0;
    frSampleHead = frSampleCount = 0;
    frBytes = 0;
    frRaised = frHold = 0;
    frEpisodeTimes = -1;
    frEpisodeLast = 0;
    frCapacity = 0;
    frLoss = 0;
    frLossAt = frLossCut = 0;
    frGoodFrames = frLostFrames = 0;
    frLossWindowStart = 0;
    frLastQueueMs = 0;
}

void LiFarloopSetAdaptive(bool enabled) {
    frEnabled = enabled;
}

// 画质档位：码率上限。开始串流前设置（编码器按 StreamConfig.bitrate 这个最高值建，马上降到上限），
// 串流中再调就是“无缝切换画质”：不重连，下一次定时消息就把新码率发给被控端
void LiFarloopSetCap(int kbps) {
    frCapKbps = kbps;
    if (!frEnabled || frTargetKbps <= 0) return;
    int ceiling = StreamConfig.bitrate > 0 ? StreamConfig.bitrate : kbps;
    int t = (kbps > 0 && kbps < ceiling) ? kbps : ceiling;
    frTargetKbps = t;
    frEstimate = t;      // 用户主动选的档位：直接到位（网络不够时控制器会马上降）
    frCapacity = 0;
    frHold = 0;
    frApplied = t;
    frPendingKbps = t;
    Limelog("Farloop: quality cap -> %d kbps\n", t);
}

int LiFarloopGetBitrateKbps(void) {
    return frEnabled ? frApplied : 0;
}

int LiFarloopGetQueueDelayMs(void) {
    return frLastQueueMs;
}

// 串流中途改帧率（被控端是 0.21 起的 Farloop 定制版 Sunshine：消息 0x55F1，采集节奏当场换、NVENC 重配置）
void LiFarloopSetFps(int fps) {
    if (fps >= 10 && fps <= 240) frPendingFps = fps;
}

// 串流中途改分辨率（被控端 Farloop 0.22 起：消息 0x55F2，编码器按新尺寸重建，第一帧是关键帧，连接不断）
void LiFarloopSetResolution(int width, int height) {
    if (width >= 320 && height >= 240 && width <= 8192 && height <= 8192) frPendingRes = (width << 16) | height;
}

int farloopRateTakePendingResolution(void) {
    int r = frPendingRes;
    frPendingRes = 0;
    return r;
}

int farloopRateTakePendingFps(void) {
    int f = frPendingFps;
    frPendingFps = 0;
    return f;
}

// 每次开始串流时（initializeControlStream）调用
void farloopRateInit(void) {
    frPendingFps = 0;
    frPendingRes = 0;
    frReset();
    int ceiling = StreamConfig.bitrate > 0 ? StreamConfig.bitrate : 20000;
    frTargetKbps = (frCapKbps > 0 && frCapKbps < ceiling) ? frCapKbps : ceiling;
    frEstimate = frTargetKbps;
    frApplied = frTargetKbps;
    frSentKbps = ceiling;              // 编码器是按最高值建的
    frPendingKbps = frTargetKbps;      // 有上限时马上降到上限
}

// 被控端要改成多少（0 = 不用发）。控制流的定时线程调用
int farloopRateTakePending(void) {
    if (!frEnabled || !IS_SUNSHINE()) return 0;
    int p = frPendingKbps;
    if (p <= 0 || p == frSentKbps) return 0;
    frSentKbps = p;
    return p;
}

static void frPushMin(double owd, uint64_t now) {
    // 单调队列：保留可能成为窗口最小值的样本
    while (frMinCount > 0 && frMins[frMinCount - 1].owd >= owd) frMinCount--;
    if (frMinCount == FR_MINS) { memmove(frMins, frMins + 1, sizeof(FR_MIN) * (FR_MINS - 1)); frMinCount--; }
    frMins[frMinCount].owd = owd;
    frMins[frMinCount].at = now;
    frMinCount++;
    int drop = 0;
    while (drop < frMinCount - 1 && now - frMins[drop].at > FR_MIN_OWD_WINDOW_MS) drop++;
    if (drop > 0) { memmove(frMins, frMins + drop, sizeof(FR_MIN) * (frMinCount - drop)); frMinCount -= drop; }
}

static FR_SAMPLE* frAt(int i) { // 0 = 最早
    return &frSamples[(frSampleHead + i) % FR_SAMPLES];
}

static void frPushSample(uint64_t arrived, double queue) {
    if (frSampleCount > 0 && arrived - frAt(frSampleCount - 1)->arrived > FR_RATE_SPAN_MS) {
        frSampleHead = frSampleCount = 0; // 画面静止过：之前的样本接不上
    }
    if (frSampleCount == FR_SAMPLES) { frSampleHead = (frSampleHead + 1) % FR_SAMPLES; frSampleCount--; }
    FR_SAMPLE* s = &frSamples[(frSampleHead + frSampleCount) % FR_SAMPLES];
    s->arrived = arrived;
    s->before = frBytes;
    s->queue = queue;
    frSampleCount++;
    while (frSampleCount > 1 && arrived - frAt(0)->arrived > FR_RATE_SPAN_MS) {
        frSampleHead = (frSampleHead + 1) % FR_SAMPLES;
        frSampleCount--;
    }
}

// 排着队那几段里的交付速率 = 带宽（kbps），没有就返回 0
static double frBusyKbps(void) {
    int n = frSampleCount, start = n - (FR_STANDING + 1);
    if (start < 0) start = 0;
    uint64_t bytes = 0, ms = 0;
    for (int i = start; i + 1 < n; i++) {
        FR_SAMPLE* a = frAt(i);
        FR_SAMPLE* b = frAt(i + 1);
        uint64_t gap = b->arrived - a->arrived;
        if (b->queue >= (double)gap) { bytes += b->before - a->before; ms += gap; }
    }
    return (ms > 0 && bytes > 0) ? bytes * 8.0 / ms : 0; // 字节/毫秒 *8 = kbps
}

// 编码器最近实际用了多少（kbps），没有就返回 -1
static double frUsedKbps(void) {
    if (frSampleCount < 2) return -1;
    FR_SAMPLE* a = frAt(0);
    FR_SAMPLE* b = frAt(frSampleCount - 1);
    uint64_t ms = b->arrived - a->arrived;
    return ms >= FR_MIN_RATE_SPAN_MS ? (b->before - a->before) * 8.0 / ms : -1;
}

static bool frLossy(uint64_t now) {
    return frLossAt != 0 && frLoss > FR_LOSS_LOW && now < frLossAt + FR_LOSS_TTL_MS;
}

static void frOnLoss(double loss, uint64_t now) {
    frLoss = loss;
    frLossAt = now;
    if (loss <= FR_LOSS_HIGH || (frLossCut != 0 && now < frLossCut + FR_LOSS_CUT_EVERY_MS)) return;
    frLossCut = now;
    frRaised = 0;
    double next = frEstimate * (1.0 - (loss > 1 ? 1 : loss) / 2.0);
    frEstimate = next > frFloor() ? next : frFloor();
    frApply();
}

// 返回 true = 排队降不下来，重新取基准
static bool frUpdate(uint64_t now, double standing, double busy, double used, double srtt) {
    bool held = frHold != 0 && now < frHold;
    double waitMs = srtt + standing;
    if (waitMs < 100) waitMs = 100;
    if (waitMs > 1000) waitMs = 1000;

    if (standing > FR_QUEUE_HIGH_MS) {
        frRaised = 0;
        if (held) return false;
        if (frEpisodeTimes >= 0 && standing < frEpisodeLast * 0.8) {
            // 降过之后正在消下去：再等等
            frEpisodeLast = standing;
            frHold = now + (uint64_t)waitMs;
            return false;
        }
        if (frEpisodeTimes >= 2 && standing <= frEpisodeLast * 1.1) {
            // 降了两次还是不见少：不是我们发多了（比如路由变了），换基准
            frEpisodeTimes = -1;
            frHold = 0;
            return true;
        }
        // 编码器实际用的码率远低于估计值时（画面静止、帧很小），量到的“带宽”只是画面本身的码率，不能拿来定码率
        bool linkBusy = busy > 0 && used > 0 && used >= frEstimate * 0.5;
        double next = linkBusy ? (busy * 0.85 < frEstimate * 0.9 ? busy * 0.85 : frEstimate * 0.9) : frEstimate * 0.85;
        if (next < frEstimate * 0.7) next = frEstimate * 0.7; // 一次最多降 30%，抖一下不会掉到底
        frEstimate = next > frFloor() ? next : frFloor();
        if (busy > 0) frCapacity = busy;
        frEpisodeTimes = (frEpisodeTimes < 0 ? 0 : frEpisodeTimes) + 1;
        frEpisodeLast = standing;
        frHold = now + (uint64_t)waitMs;
    }
    else if (standing < FR_QUEUE_LOW_MS) {
        frEpisodeTimes = -1;
        if (held) { frRaised = 0; return false; }
        double dt = 0;
        if (frRaised != 0) { dt = (now - frRaised) / 1000.0; if (dt > 0.2) dt = 0.2; }
        frRaised = now;
        if (used >= frEstimate * 0.5 && !frLossy(now)) {
            bool near = frCapacity > 0 && frEstimate >= frCapacity * 0.9;
            double perSec = near ? 0.04 : 0.15;
            frEstimate = frEstimate * (1.0 + perSec * dt) + 100.0 * dt;
            if (frEstimate > frTargetKbps) frEstimate = frTargetKbps;
            if (frCapacity > 0 && frEstimate > frCapacity * 1.2) frCapacity = 0;
        }
    }
    else {
        frRaised = 0; // 排着一点队：不加也不降
    }
    frApply();
    return false;
}

// 收齐了一帧（VideoDepacketizer 调用）
// receiveMs：第一个包到达（本机时钟），ptsMs：采集时刻（被控端时钟），hostLatency：被控端采集到发出（0.1 ms），bytes：帧大小
void farloopRateOnFrame(uint64_t receiveMs, unsigned int ptsMs, uint16_t hostLatency, int bytes) {
    if (!frEnabled || receiveMs == 0) return;
    uint64_t now = PltGetMillis();
    double sent = (double)ptsMs + hostLatency / 10.0;
    double owd = (double)receiveMs - sent;
    frPushMin(owd, now);
    double queue = owd - frMins[0].owd;
    if (queue < 0) queue = 0;
    frLastQueueMs = (int)queue;
    frPushSample(receiveMs, queue);
    frBytes += (uint64_t)(bytes > 0 ? bytes : 0);

    // 丢帧率：大约每秒结一次
    frGoodFrames++;
    if (frLossWindowStart == 0) frLossWindowStart = now;
    if (now - frLossWindowStart >= 1000) {
        uint32_t total = frGoodFrames + frLostFrames;
        if (total >= 20) frOnLoss((double)frLostFrames / total, now);
        frGoodFrames = frLostFrames = 0;
        frLossWindowStart = now;
    }

    if (frSampleCount < FR_STANDING) return;
    double standing = 1e9;
    for (int i = frSampleCount - FR_STANDING; i < frSampleCount; i++)
        if (frAt(i)->queue < standing) standing = frAt(i)->queue;
    uint32_t rtt = 0, rttVar = 0;
    double srtt = LiGetEstimatedRttInfo(&rtt, &rttVar) ? rtt : 100.0;
    if (frUpdate(now, standing, frBusyKbps(), frUsedKbps(), srtt)) {
        frMinCount = 0;
        frPushMin(owd, now);
        frSampleHead = frSampleCount = 0;
    }
}

// 网络丢了几帧（ControlStream 的 connectionDetectedFrameLoss 调用）
void farloopRateOnFrameLoss(uint32_t count) {
    if (!frEnabled) return;
    if (count > 1000) count = 1000;
    frLostFrames += count;
}
