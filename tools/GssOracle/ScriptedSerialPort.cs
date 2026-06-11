using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GS.Shared.Transport;

namespace GssOracle
{
    /// <summary>
    /// Scripted ISerialPort fed to GSServer's SkyQueue: records every command GSS
    /// writes (the oracle transcript) and replies like an EQ6 motor controller
    /// (CPR 9024000, timer 1500000, ratio 16, worm 50133, fw 3.39 - the same canned
    /// mount TianWen's FakeSkywatcherSerialDevice models). Replies are minimal but
    /// stateful enough for GSS's status-driven branches: :f reports running/forward/
    /// high-speed from the latest :G/:J/:K, and GOTOs arrive instantly so
    /// AxisGoToTarget's poll loop terminates.
    /// </summary>
    public sealed class ScriptedSerialPort : ISerialPort
    {
        public sealed class Entry
        {
            public string Scenario;
            public string Command;
            public string Reply;
        }

        private sealed class AxisState
        {
            public bool Running;
            public int Func = 1;
            public bool Forward = true;
            public bool South;
            public long Pos = 0x800000;
            public long GotoTarget = 0x800000;
        }

        public List<Entry> Transcript { get; } = new List<Entry>();
        public string Scenario { get; set; } = "init";

        private readonly AxisState[] _axes = { new AxisState(), new AxisState() };
        private string _pending = string.Empty;
        private readonly object _gate = new object();

        public int ReadTimeout => 1000;
        public bool IsOpen => true;
        public void Open() { }
        public void Dispose() { }
        public void DiscardInBuffer() { }
        public void DiscardOutBuffer() { }

        public string ReadExisting()
        {
            lock (_gate)
            {
                var r = _pending;
                _pending = string.Empty;
                return r;
            }
        }

        public void Write(string data)
        {
            lock (_gate)
            {
                var cmd = data.TrimEnd('\r');
                var reply = Respond(cmd);
                Transcript.Add(new Entry { Scenario = Scenario, Command = cmd, Reply = reply });
                _pending = reply + "\r";
            }
        }

        private string Respond(string cmd)
        {
            // ":<cmd char><axis char><payload>"
            if (cmd.Length < 3 || cmd[0] != ':') return "!3";
            var c = cmd[1];
            var axis = _axes[cmd[2] == '2' ? 1 : 0];
            var payload = cmd.Length > 3 ? cmd.Substring(3) : string.Empty;

            switch (c)
            {
                case 'e': return "=002703";            // EQ6, fw 3.39
                case 'a': return "=00B289";            // CPR 9024000 = 0x89B200 (LE hex)
                case 'b': return "=60E316";            // timer freq 1500000
                case 'g': return "=10";                // high-speed ratio 16
                case 's': return "=D5C300";            // worm steps 50133
                case 'f':
                {
                    // 3 status nibbles: [0] bit0=slewing(constant) bit1=reverse bit2=highspeed,
                    // [1] bit0=running, [2] bit0=initialized.
                    var slewMode = axis.Func == 1 || axis.Func == 3;
                    var high = axis.Func == 0 || axis.Func == 3;
                    var n1 = (slewMode ? 1 : 0) | (axis.Forward ? 0 : 2) | (high ? 4 : 0);
                    var n2 = axis.Running ? 1 : 0;
                    return "=" + n1.ToString("X1") + n2 + "1";
                }
                case 'j': return "=" + Le6(axis.Pos);
                case 'd': return "=" + Le6(axis.Pos);  // secondary encoder mirrors primary
                case 'G':
                {
                    if (payload.Length >= 2)
                    {
                        axis.Func = payload[0] - '0';
                        var dir = payload[1] - '0';
                        axis.Forward = (dir & 0x1) == 0;
                        axis.South = (dir & 0x2) != 0;
                    }
                    return "=";
                }
                case 'H':
                {
                    var steps = ParseLe6(payload);
                    axis.GotoTarget = axis.Forward ? axis.Pos + steps : axis.Pos - steps;
                    return "=";
                }
                case 'S':
                    axis.GotoTarget = ParseLe6(payload);
                    return "=";
                case 'E':
                    axis.Pos = ParseLe6(payload);
                    return "=";
                case 'I': return "=";
                case 'J':
                {
                    if (axis.Func == 0 || axis.Func == 2)
                    {
                        // Instant GOTO so the caller's FullStop poll terminates at once.
                        axis.Pos = axis.GotoTarget;
                        axis.Running = false;
                    }
                    else
                    {
                        axis.Running = true;
                    }
                    return "=";
                }
                case 'K':
                case 'L':
                    axis.Running = false;
                    return "=";
                case 'q': return "=000000";            // no extended capabilities
                default:
                    // Action commands (uppercase) ack with bare "="; unknown queries get zeros.
                    return char.IsUpper(c) ? "=" : "=000000";
            }
        }

        private static string Le6(long value)
        {
            var v = value & 0xFFFFFF;
            return ((byte)(v & 0xFF)).ToString("X2")
                 + ((byte)((v >> 8) & 0xFF)).ToString("X2")
                 + ((byte)((v >> 16) & 0xFF)).ToString("X2");
        }

        private static long ParseLe6(string hex)
        {
            if (hex.Length < 6) return 0;
            var b0 = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b1 = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var b2 = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b0 | ((long)b1 << 8) | ((long)b2 << 16);
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (var i = 0; i < Transcript.Count; i++)
            {
                var e = Transcript[i];
                sb.Append("  { \"scenario\": \"").Append(e.Scenario)
                  .Append("\", \"cmd\": \"").Append(e.Command)
                  .Append("\", \"reply\": \"").Append(e.Reply)
                  .Append(i < Transcript.Count - 1 ? "\" }," : "\" }")
                  .AppendLine();
            }
            sb.AppendLine("]");
            return sb.ToString();
        }
    }
}
