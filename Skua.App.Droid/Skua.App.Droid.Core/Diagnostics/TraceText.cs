using System.Globalization;
using System.Text;

namespace Skua.App.Droid.Core.Diagnostics;

/// <summary>
/// Turns the trace Android keeps with an exit record (ApplicationExitInfo.getTraceInputStream) into readable text:
/// ANR traces are text already; native crash tombstones (Android 12+) are protobuf (system/core/debuggerd
/// tombstone.proto), decoded here into the familiar "signal / abort message / backtrace / log" layout. Anything else
/// is reduced to its printable strings. Never throws; the output is bounded.
/// </summary>
public static class TraceText
{
    /// <summary>How much of a trace stream is read at most.</summary>
    public const int MaxInputBytes = 4 * 1024 * 1024;

    public const int MaxOutputChars = ProcessExitReport.MaxTraceChars;

    private const int MaxOtherThreads = 40;
    private const int OtherThreadFrames = 6;
    private const int CrashThreadFrames = 64;
    private const int LogLines = 80;

    /// <param name="data">The bytes read (at most <see cref="MaxInputBytes"/>).</param>
    /// <param name="truncated">More bytes were available than were read.</param>
    public static string Format(byte[] data, bool truncated)
    {
        try
        {
            string text;
            if (data.Length == 0)
                return "(empty trace)";
            if (LooksLikeText(data))
                text = Encoding.UTF8.GetString(data);
            else if (TryFormatTombstone(data, out string? tombstone))
                text = tombstone;
            else
                text = "(binary trace that is not a tombstone; printable strings follow)\n" + PrintableStrings(data);
            if (truncated)
                text += $"\n...(only the first {data.Length / 1024} kB of the trace were read)";
            return text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n...(trace truncated)";
        }
        catch (Exception e)
        {
            return $"(the trace could not be decoded: {e.GetType().Name})";
        }
    }

    /// <summary>Text if (nearly) every byte of the first 4 kB is printable UTF-8 text.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, 4096)];
        int bad = 0;
        foreach (byte b in head)
        {
            if (b is < 0x20 and not (byte)'\n' and not (byte)'\r' and not (byte)'\t')
                bad++;
        }
        return bad * 100 <= head.Length; // at most 1 %
    }

    /// <summary>Decodes a tombstone.proto Tombstone message; false when <paramref name="data"/> is not one.</summary>
    public static bool TryFormatTombstone(ReadOnlySpan<byte> data, out string text)
    {
        text = string.Empty;
        Tombstone t = new();
        try
        {
            ProtoReader reader = new(data.ToArray());
            while (reader.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 2 when wire == 2: t.Fingerprint = reader.String(); break;
                    case 3 when wire == 2: t.Revision = reader.String(); break;
                    case 4 when wire == 2: t.Timestamp = reader.String(); break;
                    case 5 when wire == 0: t.Pid = (long)reader.Varint(); t.HasPid = true; break;
                    case 6 when wire == 0: t.Tid = (long)reader.Varint(); break;
                    case 9 when wire == 2: t.CommandLine.Add(reader.String()); break;
                    case 10 when wire == 2: t.Signal = ParseSignal(reader.Bytes()); break;
                    case 14 when wire == 2: t.AbortMessage = reader.String(); break;
                    case 15 when wire == 2: t.Causes.Add(ParseCause(reader.Bytes())); break;
                    case 16 when wire == 2: ParseThreadEntry(reader.Bytes(), t.Threads); break;
                    case 18 when wire == 2: t.Logs.Add(ParseLogBuffer(reader.Bytes())); break;
                    case 20 when wire == 0: t.Uptime = (long)reader.Varint(); break;
                    default: reader.Skip(wire); break;
                }
            }
        }
        catch (FormatException)
        {
            return false;
        }
        if (!t.HasPid || (t.Threads.Count == 0 && t.Signal is null))
            return false;
        text = Render(t);
        return true;
    }

    /// <summary>Runs of at least six printable ASCII characters, one per line (like strings(1)), bounded.</summary>
    public static string PrintableStrings(ReadOnlySpan<byte> data)
    {
        StringBuilder sb = new();
        StringBuilder run = new();
        foreach (byte b in data)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                run.Append((char)b);
                continue;
            }
            Flush();
            if (sb.Length > MaxOutputChars)
                break;
        }
        Flush();
        return sb.ToString();

        void Flush()
        {
            if (run.Length >= 6)
                sb.Append(run).Append('\n');
            run.Clear();
        }
    }

    private static string Render(Tombstone t)
    {
        StringBuilder sb = new();
        sb.Append("Native crash tombstone\n");
        if (t.Fingerprint is not null)
            sb.Append("Build fingerprint: '").Append(t.Fingerprint).Append("'\n");
        if (t.Revision is not null)
            sb.Append("Revision: '").Append(t.Revision).Append("'\n");
        if (t.Timestamp is not null)
            sb.Append("Timestamp: ").Append(t.Timestamp).Append('\n');
        sb.Append(FormattableString.Invariant($"pid: {t.Pid}, tid: {t.Tid}, process uptime: {t.Uptime}s\n"));
        if (t.CommandLine.Count > 0)
            sb.Append("Cmdline: ").Append(string.Join(' ', t.CommandLine)).Append('\n');
        if (t.Threads.TryGetValue(t.Tid, out TombThread? crashing))
            sb.Append("Thread: ").Append(crashing.Name).Append('\n');
        if (t.Signal is { } s)
        {
            sb.Append(FormattableString.Invariant($"signal {s.Number} ({s.Name}), code {s.Code} ({s.CodeName})"));
            if (s.HasFaultAddress)
                sb.Append(FormattableString.Invariant($", fault addr 0x{s.FaultAddress:x}"));
            if (s.HasSender)
                sb.Append(FormattableString.Invariant($", from pid {s.SenderPid}, uid {s.SenderUid}"));
            sb.Append('\n');
        }
        if (!string.IsNullOrEmpty(t.AbortMessage))
            sb.Append("Abort message: '").Append(t.AbortMessage).Append("'\n");
        foreach (string cause in t.Causes.Where(c => c.Length > 0))
            sb.Append("Cause: ").Append(cause).Append('\n');
        if (crashing is not null
            && MonoGcDiagnostics.Diagnose(crashing.Frames.Select(f => (f.File, f.BuildId, f.RelPc)).ToList(), crashing.Name, MonoGcDiagnostics.CurrentGcParams) is string diagnosis)
            sb.Append("Diagnosis: ").Append(diagnosis).Append('\n');

        if (crashing is not null)
        {
            sb.Append("\nbacktrace (crashing thread ").Append(crashing.Id.ToString(CultureInfo.InvariantCulture)).Append(" \"").Append(crashing.Name).Append("\"):\n");
            AppendFrames(sb, crashing, CrashThreadFrames);
        }

        List<TombThread> others = t.Threads.Values.Where(th => th.Id != t.Tid).OrderBy(th => th.Id).ToList();
        if (others.Count > 0)
        {
            sb.Append(FormattableString.Invariant($"\nother threads ({others.Count}, top {OtherThreadFrames} frames each"))
                .Append(others.Count > MaxOtherThreads ? FormattableString.Invariant($", first {MaxOtherThreads} shown") : string.Empty).Append("):\n");
            foreach (TombThread th in others.Take(MaxOtherThreads))
            {
                sb.Append("  tid ").Append(th.Id.ToString(CultureInfo.InvariantCulture)).Append(" \"").Append(th.Name).Append("\"\n");
                AppendFrames(sb, th, OtherThreadFrames);
            }
        }

        List<string> log = t.Logs.SelectMany(b => b.Lines.Select(l => $"{b.Name} {l}")).ToList();
        if (log.Count > 0)
        {
            sb.Append(FormattableString.Invariant($"\nlog (last {Math.Min(LogLines, log.Count)} of {log.Count} lines):\n"));
            foreach (string line in log.Skip(Math.Max(0, log.Count - LogLines)))
                sb.Append("  ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendFrames(StringBuilder sb, TombThread thread, int max)
    {
        int i = 0;
        foreach (Frame f in thread.Frames.Take(max))
        {
            sb.Append(FormattableString.Invariant($"    #{i:00} pc {f.RelPc:x16}  {f.File ?? "<unknown>"}"));
            if (!string.IsNullOrEmpty(f.Function))
                sb.Append(FormattableString.Invariant($" ({f.Function}+{f.FunctionOffset})"));
            else if (MonoGcDiagnostics.Symbolize(f.File, f.BuildId, f.RelPc) is string symbol)
                sb.Append(" [").Append(symbol).Append(']');
            if (!string.IsNullOrEmpty(f.BuildId))
                sb.Append(" (BuildId: ").Append(f.BuildId).Append(')');
            sb.Append('\n');
            i++;
        }
        if (thread.Frames.Count > max)
            sb.Append(FormattableString.Invariant($"    ...({thread.Frames.Count - max} more frames)\n"));
        foreach (string note in thread.Notes)
            sb.Append("    note: ").Append(note).Append('\n');
    }

    private static Signal ParseSignal(byte[] bytes)
    {
        Signal s = new();
        ProtoReader r = new(bytes);
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: s.Number = (int)r.Varint(); break;
                case 2 when wire == 2: s.Name = r.String(); break;
                case 3 when wire == 0: s.Code = (int)r.Varint(); break;
                case 4 when wire == 2: s.CodeName = r.String(); break;
                case 5 when wire == 0: s.HasSender = r.Varint() != 0; break;
                case 6 when wire == 0: s.SenderUid = (int)r.Varint(); break;
                case 7 when wire == 0: s.SenderPid = (int)r.Varint(); break;
                case 8 when wire == 0: s.HasFaultAddress = r.Varint() != 0; break;
                case 9 when wire == 0: s.FaultAddress = r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        return s;
    }

    private static string ParseCause(byte[] bytes)
    {
        ProtoReader r = new(bytes);
        string text = string.Empty;
        while (r.Next(out int field, out int wire))
        {
            if (field == 1 && wire == 2)
                text = r.String();
            else
                r.Skip(wire);
        }
        return text;
    }

    private static void ParseThreadEntry(byte[] bytes, Dictionary<long, TombThread> threads)
    {
        ProtoReader r = new(bytes);
        long key = -1;
        TombThread? thread = null;
        while (r.Next(out int field, out int wire))
        {
            if (field == 1 && wire == 0)
                key = (long)r.Varint();
            else if (field == 2 && wire == 2)
                thread = ParseThread(r.Bytes());
            else
                r.Skip(wire);
        }
        if (thread is not null)
            threads[key >= 0 ? key : thread.Id] = thread;
    }

    private static TombThread ParseThread(byte[] bytes)
    {
        TombThread th = new();
        ProtoReader r = new(bytes);
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: th.Id = (long)r.Varint(); break;
                case 2 when wire == 2: th.Name = r.String(); break;
                case 4 when wire == 2: th.Frames.Add(ParseFrame(r.Bytes())); break;
                case 7 when wire == 2: th.Notes.Add(r.String()); break;
                default: r.Skip(wire); break;
            }
        }
        return th;
    }

    private static Frame ParseFrame(byte[] bytes)
    {
        Frame f = new();
        ProtoReader r = new(bytes);
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: f.RelPc = r.Varint(); break;
                case 4 when wire == 2: f.Function = r.String(); break;
                case 5 when wire == 0: f.FunctionOffset = r.Varint(); break;
                case 6 when wire == 2: f.File = r.String(); break;
                case 8 when wire == 2: f.BuildId = r.String(); break;
                default: r.Skip(wire); break;
            }
        }
        return f;
    }

    private static LogBuffer ParseLogBuffer(byte[] bytes)
    {
        LogBuffer buffer = new();
        ProtoReader r = new(bytes);
        while (r.Next(out int field, out int wire))
        {
            if (field == 1 && wire == 2)
                buffer.Name = r.String();
            else if (field == 2 && wire == 2)
                buffer.Lines.Add(ParseLogMessage(r.Bytes()));
            else
                r.Skip(wire);
        }
        return buffer;
    }

    private static string ParseLogMessage(byte[] bytes)
    {
        ProtoReader r = new(bytes);
        string timestamp = string.Empty, tag = string.Empty, message = string.Empty;
        ulong pid = 0, tid = 0, priority = 0;
        while (r.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2: timestamp = r.String(); break;
                case 2 when wire == 0: pid = r.Varint(); break;
                case 3 when wire == 0: tid = r.Varint(); break;
                case 4 when wire == 0: priority = r.Varint(); break;
                case 5 when wire == 2: tag = r.String(); break;
                case 6 when wire == 2: message = r.String(); break;
                default: r.Skip(wire); break;
            }
        }
        string level = priority switch { 2 => "V", 3 => "D", 4 => "I", 5 => "W", 6 => "E", 7 => "F", _ => "?" };
        return FormattableString.Invariant($"{timestamp} {pid,5} {tid,5} {level} {tag}: {message.TrimEnd('\n')}");
    }

    private sealed class Tombstone
    {
        public string? Fingerprint, Revision, Timestamp, AbortMessage;
        public long Pid, Tid, Uptime;
        public bool HasPid;
        public Signal? Signal;
        public readonly List<string> CommandLine = new();
        public readonly List<string> Causes = new();
        public readonly Dictionary<long, TombThread> Threads = new();
        public readonly List<LogBuffer> Logs = new();
    }

    private sealed class Signal
    {
        public int Number, Code, SenderUid, SenderPid;
        public string Name = "?", CodeName = "?";
        public bool HasSender, HasFaultAddress;
        public ulong FaultAddress;
    }

    private sealed class TombThread
    {
        public long Id;
        public string Name = "?";
        public readonly List<Frame> Frames = new();
        public readonly List<string> Notes = new();
    }

    private sealed class Frame
    {
        public ulong RelPc, FunctionOffset;
        public string? Function, File, BuildId;
    }

    private sealed class LogBuffer
    {
        public string Name = "log";
        public readonly List<string> Lines = new();
    }

    /// <summary>Minimal protobuf wire-format reader; throws <see cref="FormatException"/> on malformed input.</summary>
    private sealed class ProtoReader
    {
        private readonly byte[] _data;
        private int _pos;

        public ProtoReader(byte[] data)
        {
            _data = data;
        }

        public bool Next(out int field, out int wire)
        {
            field = 0;
            wire = 0;
            if (_pos >= _data.Length)
                return false;
            ulong tag = Varint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            if (field <= 0 || wire is 3 or 4 or 6 or 7)
                throw new FormatException("bad tag");
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (_pos >= _data.Length)
                    throw new FormatException("truncated varint");
                byte b = _data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
            }
            throw new FormatException("varint too long");
        }

        public byte[] Bytes()
        {
            ulong length = Varint();
            if (length > (ulong)(_data.Length - _pos))
                throw new FormatException("length past the end");
            byte[] bytes = _data.AsSpan(_pos, (int)length).ToArray();
            _pos += (int)length;
            return bytes;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Advance(8); break;
                case 2: Advance((long)Varint()); break;
                case 5: Advance(4); break;
                default: throw new FormatException("bad wire type");
            }
        }

        private void Advance(long count)
        {
            if (count < 0 || count > _data.Length - _pos)
                throw new FormatException("skip past the end");
            _pos += (int)count;
        }
    }
}
