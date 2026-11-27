using System;
using System.Runtime.InteropServices;

namespace UsageTracker
{
    /// <summary>
    /// 게임패드(XInput, Xbox 호환) 입력도 "사용 중"으로 본다. Windows의 마지막 입력 시각(GetLastInputInfo)에는
    /// 게임패드가 들어가지 않기 때문이다. 자리 비움 판정 때만(1분 주기·자리 비움 중 2초 간격) 상태 번호를 비교한다.
    /// 연결되지 않은 칸은 확인 비용이 있어 1분에 한 번만 다시 본다. xinput1_4.dll이 없으면 조용히 끈다.
    /// </summary>
    static class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

        [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint index, out XINPUT_STATE state);

        static readonly uint[] lastPacket = new uint[4];
        static readonly bool[] connected = new bool[4];
        static int lastFullScan, lastInputTick;
        static bool scanned, disabled, hadInput;

        const uint MaxAgeMs = 24U * 3600U * 1000U;   // 24시간 이상 입력이 없으면 초기화 (49.7일 TickCount 순환 방지)

        /// <summary>마지막 게임패드 입력 이후 경과(ms). 입력을 본 적이 없으면 uint.MaxValue.</summary>
        public static uint MsSinceInput()
        {
            if (disabled) return uint.MaxValue;
            int now = Environment.TickCount;
            bool full = !scanned || unchecked((uint)(now - lastFullScan)) >= 60000;
            if (full) { lastFullScan = now; scanned = true; }
            try
            {
                for (uint i = 0; i < 4; i++)
                {
                    if (!connected[i] && !full) continue;
                    XINPUT_STATE s;
                    if (XInputGetState(i, out s) != 0) { connected[i] = false; continue; }
                    // 처음 연결을 알아챘을 때는 입력으로 치지 않는다 (기준값만 잡음)
                    if (connected[i] && s.dwPacketNumber != lastPacket[i]) { lastInputTick = now; hadInput = true; }
                    connected[i] = true;
                    lastPacket[i] = s.dwPacketNumber;
                }
            }
            catch (Exception ex)
            {
                disabled = true;   // DllNotFound 등: 이 PC에서는 게임패드를 보지 않는다
                if (!(ex is DllNotFoundException || ex is EntryPointNotFoundException)) ErrorLog.Write("gamepad", ex);
                return uint.MaxValue;
            }
            if (!hadInput) return uint.MaxValue;
            bool expired;
            uint elapsed = ClampElapsed(unchecked((uint)(now - lastInputTick)), out expired);
            if (expired) hadInput = false;
            return elapsed;
        }

        internal static uint ClampElapsed(uint elapsedMs, out bool expired)
        {
            if (elapsedMs >= MaxAgeMs) { expired = true; return uint.MaxValue; }
            expired = false;
            return elapsedMs;
        }
    }
}
