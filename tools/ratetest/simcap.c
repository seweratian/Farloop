#include "fake/Limelight-internal.h"
FAKE_STREAM_CONFIG StreamConfig;
uint64_t fakeNow = 1000;
int verbose = 0;
bool LiGetEstimatedRttInfo(uint32_t* rtt, uint32_t* var) { *rtt = 40; *var = 5; return true; }
#include "fake/FarloopRate.c"
int main(void) {
    StreamConfig.bitrate = 60000;           // 编码器按最高值建
    LiFarloopSetAdaptive(true);
    LiFarloopSetCap(20000);                 // 开始前选了“标准”
    farloopRateInit();
    int first = farloopRateTakePending();
    printf("start: first send %d kbps (expect 20000)\n", first);
    double linkFreeAt = 0; int enc = first, lost = 0;
    for (int t = 0; t < 60000; t++) {
        fakeNow = 1000 + t;
        if (t == 20000) LiFarloopSetCap(35000);   // 串流中换“高清”
        if (t == 40000) LiFarloopSetCap(8000);    // 换“流畅”
        if (t % 100 == 0) { int k = farloopRateTakePending(); if (k) enc = k; }
        if (t % 16) continue;
        int bytes = enc * 1000 / 8 / 60; double cap = 100000;   // 链路很宽：只看档位
        double start = linkFreeAt > t ? linkFreeAt : t;
        if (start - t > 300) { lost++; farloopRateOnFrameLoss(1); continue; }
        linkFreeAt = start + bytes * 8.0 / cap;
        farloopRateOnFrame((uint64_t)(1000 + start + 20), (unsigned)(t + 7777), 30, bytes);
        if (t == 19984 || t == 20304 || t == 39984 || t == 40304 || t == 59984)
            printf("t=%5.1fs encoder=%5.1fM\n", t / 1000.0, enc / 1000.0);
    }
    printf("lost=%d\n", lost);
}
