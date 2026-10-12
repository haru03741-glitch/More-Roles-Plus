#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define MRP_API extern "C" __declspec(dllexport)
#else
#define MRP_API extern "C" __attribute__((visibility("default")))
#endif

// C# 側 (src/Native/MrpNative.cs の Abi) と同じ数。関数の形を変えたら上げる
#define MRP_NATIVE_ABI 13
