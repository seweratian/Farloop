#include "fake/Limelight-internal.h"
#include <stdlib.h>
FAKE_STREAM_CONFIG StreamConfig;
uint64_t fakeNow = 1000;
int verbose = 0;
bool LiGetEstimatedRttInfo(uint32_t* rtt, uint32_t* var) { *rtt = 60; *var = 20; return true; }
#include "fake/FarloopRate.c"
// 5G：链路 50M 够用，但每帧随机多 0~50 ms 的抖动，偶尔一阵 300 ms 的尖峰；画面一半时间静止（帧很小）
int main(void) {
    srand(7);
    StreamConfig.bitrate = 60000;
    LiFarloopSetAdaptive(true);
    LiFarloopSetCap(8000);          // 用户选“流畅”
    farloopRateInit();
    int enc = farloopRateTakePending(), lost = 0, minEnc = 1 << 30;
    double linkFreeAt = 0;
    for (int t = 0; t < 120000; t++) {
        fakeNow = 1000 + t;
        if (t % 100 == 0) { int k = farloopRateTakePending(); if (k) enc = k; }
        if (t % 16) continue;
        int still = (t / 5000) % 2;          // 每 5 秒切换：静止 / 活动画面
        int bytes = still ? 2000 : enc * 1000 / 8 / 60;
        double cap = 50000;
        double start = linkFreeAt > t ? linkFreeAt : t;
        linkFreeAt = start + bytes * 8.0 / cap;
        double jitter = rand() % 50 + ((t % 20000) < 300 ? 300 : 0);
        farloopRateOnFrame((uint64_t)(1000 + start + 20 + jitter), (unsigned)(t + 7777), 30, bytes);
        if (t > 5000 && enc < minEnc) minEnc = enc;
        if (t % 20000 == 0) printf("t=%3ds encoder=%5.1fM\n", t / 1000, enc / 1000.0);
    }
    printf("lowest after 5s: %.1fM (cap 8M; was ~1.3M before)\n", minEnc / 1000.0);
}
