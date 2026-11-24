#include "fake/Limelight-internal.h"
FAKE_STREAM_CONFIG StreamConfig;
uint64_t fakeNow = 1000;
int verbose = 0;
bool LiGetEstimatedRttInfo(uint32_t* rtt, uint32_t* var) { *rtt = 40; *var = 5; return true; }
#include "fake/FarloopRate.c"

// 瓶颈链路：容量 cap(t) kbps，缓冲 300 ms，单程基础时延 20 ms；被控端 60 fps，码率 = 控制端最近发的（RTT/2 后生效）
static int capAt(int ms) { return ms < 20000 ? 30000 : ms < 40000 ? 12000 : 30000; }

int main(int argc, char** argv) {
    verbose = argc > 1;
    StreamConfig.bitrate = 60000;
    LiFarloopSetAdaptive(true);
    farloopRateInit();
    double linkFreeAt = 0; // 链路忙到什么时候（ms，被控端时钟 = 本机时钟 + 7777 的偏移）
    int encKbps = 60000, pendingAt = -1, pendingKbps = 0;
    int lost = 0, frames = 0;
    for (int t = 0; t < 60000; t += 1) {
        fakeNow = 1000 + t;
        if (t % 100 == 0) { int k = farloopRateTakePending(); if (k) { pendingKbps = k; pendingAt = t + 20; } }
        if (pendingAt >= 0 && t >= pendingAt) { encKbps = pendingKbps; pendingAt = -1; }
        if (t % 16 != 0) continue;
        frames++;
        int bytes = encKbps * 1000 / 8 / 60;
        double cap = capAt(t);                 // kbps = bytes*8/ms
        double start = linkFreeAt > t ? linkFreeAt : t;
        double queueMs = start - t;
        if (queueMs > 300) { lost++; farloopRateOnFrameLoss(1); continue; } // 缓冲满：整帧丢
        linkFreeAt = start + bytes * 8.0 / cap;
        uint64_t arrive = (uint64_t)(1000 + start + 20);
        unsigned pts = (unsigned)(t + 7777);
        farloopRateOnFrame(arrive, pts, 30 /*3ms*/, bytes);
        if (t % 2000 == 0)
            printf("t=%5.1fs cap=%5.1fM enc=%5.1fM applied=%5.1fM queue=%3d ms lost=%d\n", t / 1000.0, cap / 1000, encKbps / 1000.0, LiFarloopGetBitrateKbps() / 1000.0, LiFarloopGetQueueDelayMs(), lost);
    }
    printf("frames=%d lost=%d\n", frames, lost);
    return 0;
}
