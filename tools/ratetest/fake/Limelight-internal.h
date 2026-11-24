#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <stdio.h>
typedef struct { int bitrate; } FAKE_STREAM_CONFIG;
extern FAKE_STREAM_CONFIG StreamConfig;
extern uint64_t fakeNow;
#define Limelog(...) do { if (verbose) printf(__VA_ARGS__); } while (0)
extern int verbose;
#define IS_SUNSHINE() 1
static inline uint64_t PltGetMillis(void) { return fakeNow; }
bool LiGetEstimatedRttInfo(uint32_t* rtt, uint32_t* var);
