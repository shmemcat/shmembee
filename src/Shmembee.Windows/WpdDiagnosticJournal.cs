#nullable disable
#pragma warning disable CA1837 // Environment.ProcessId is unavailable on .NET Framework 4.8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Shmembee.Windows
{
    internal sealed class WpdDiagnosticJournal
    {
        private const long MaximumFileBytes = 10 * 1024 * 1024;
        private const int MaximumFiles = 5;
        private readonly string path;
        private readonly string activityId;
        private readonly string operationId;
        private readonly object gate = new object();

        public WpdDiagnosticJournal(
            string path,
            string activityId,
            string operationId)
        {
            this.path = path;
            this.activityId = activityId;
            this.operationId = operationId;
        }

        public string Path => path;

        public static string ResolvePath(string diagnosticsPath)
        {
            string directory = string.IsNullOrWhiteSpace(diagnosticsPath)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Shmembee",
                    "diagnostics")
                : diagnosticsPath;
            Directory.CreateDirectory(directory);
            return System.IO.Path.Combine(directory, "wpd-diagnostics.jsonl");
        }

        public void Write(string source, string kind, IDictionary<string, string> data = null)
        {
            try
            {
                lock (gate)
                {
                    Rotate();
                    var item = new WpdDiagnosticEvent
                    {
                        TimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        Source = source,
                        Kind = kind,
                        ActivityId = activityId,
                        OperationId = operationId,
                        ProcessId = Process.GetCurrentProcess().Id,
                        Data = data == null
                            ? null
                            : new Dictionary<string, string>(data)
                    };
                    File.AppendAllText(
                        path,
                        Serialize(item) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // Diagnostics must never alter transport behavior.
            }
        }

        public static Dictionary<string, string> Data(params string[] pairs)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index + 1 < pairs.Length; index += 2)
            {
                if (pairs[index + 1] != null)
                {
                    result[pairs[index]] = pairs[index + 1];
                }
            }
            return result;
        }

        public static void AddException(
            IDictionary<string, string> data,
            Exception exception)
        {
            int depth = 0;
            int? deepestCom = null;
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                string prefix = "exception." + depth.ToString(CultureInfo.InvariantCulture) + ".";
                data[prefix + "type"] = current.GetType().FullName;
                data[prefix + "message"] = current.Message;
                data[prefix + "stack"] = current.StackTrace ?? string.Empty;
                if (current is System.Runtime.InteropServices.COMException)
                {
                    deepestCom = current.HResult;
                }
                depth++;
            }
            if (deepestCom.HasValue)
            {
                data["hresult.hex"] = "0x" + unchecked((uint)deepestCom.Value)
                    .ToString("X8", CultureInfo.InvariantCulture);
                data["hresult.decimal"] = deepestCom.Value
                    .ToString(CultureInfo.InvariantCulture);
            }
        }

        private void Rotate()
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < MaximumFileBytes)
            {
                return;
            }
            for (int index = MaximumFiles - 1; index >= 1; index--)
            {
                string destination = path + "." + index.ToString(CultureInfo.InvariantCulture);
                string source = index == 1
                    ? path
                    : path + "." + (index - 1).ToString(CultureInfo.InvariantCulture);
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }
        }

        private static string Serialize(WpdDiagnosticEvent item)
        {
            var json = new StringBuilder();
            json.Append('{');
            Property(json, "timestampUtc", item.TimestampUtc, false);
            Property(json, "source", item.Source, true);
            Property(json, "kind", item.Kind, true);
            Property(json, "activityId", item.ActivityId, true);
            Property(json, "operationId", item.OperationId, true);
            json.Append(",\"processId\":").Append(
                item.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (item.Data != null)
            {
                json.Append(",\"data\":{");
                bool comma = false;
                foreach (KeyValuePair<string, string> pair in item.Data)
                {
                    Property(json, pair.Key, pair.Value, comma);
                    comma = true;
                }
                json.Append('}');
            }
            return json.Append('}').ToString();
        }

        private static void Property(
            StringBuilder json,
            string name,
            string value,
            bool comma)
        {
            if (comma) json.Append(',');
            json.Append('"').Append(Escape(name)).Append("\":");
            if (value == null) json.Append("null");
            else json.Append('"').Append(Escape(value)).Append('"');
        }

        private static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length + 16);
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            escaped.Append("\\u").Append(
                                ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else escaped.Append(character);
                        break;
                }
            }
            return escaped.ToString();
        }
    }

    internal sealed class WpdDiagnosticEvent
    {
        public string TimestampUtc { get; set; }
        public string Source { get; set; }
        public string Kind { get; set; }
        public string ActivityId { get; set; }
        public string OperationId { get; set; }
        public int ProcessId { get; set; }
        public Dictionary<string, string> Data { get; set; }
    }
}
