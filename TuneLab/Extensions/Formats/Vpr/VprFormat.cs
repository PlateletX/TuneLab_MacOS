using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Extensions.Formats.Vpr;

internal sealed class VprFormat : IImportFormat, IExportFormat
{
    public ProjectInfo Deserialize(Stream stream) => VprUtility.Deserialize(stream);

    public void Serialize(Stream output, ProjectInfo info) => VprUtility.Serialize(output, info);
}

internal static class VprUtility
{
    const int MaxArchiveEntries = 10_000;
    const long MaxSequenceBytes = 64L * 1024 * 1024;
    const string RawPartProperty = "vpr.rawPart";
    const string RawTrackProperty = "vpr.rawTrack";
    const string RawMasterTrackProperty = "vpr.rawMasterTrack";
    const string RawVersionProperty = "vpr.rawVersion";
    const string RawNoteProperty = "vpr.rawNote";
    const string PhonemeProperty = "vpr.phoneme";

    static readonly ControllerDefinition[] ControllerDefinitions =
    [
        new("pitchBend", "PitchBend", -8192, 8191, -1, 1, 0),
        new("pitchBendSens", "PitchBendSensitivity", 0, 127, 0, 127, 2),
        new("dynamics", "Dynamics", 0, 127, -1, 1, 0),
        new("brightness", "Brightness", 0, 127, -1, 1, 0),
        new("character", "Gender", -64, 64, 1, -1, 0),
        new("growl", "Growl", 0, 127, 0, 1, 0),
        new("clearness", "Clearness", 0, 127, 0, 1, 0),
        new("exciter", "Exciter", -64, 63, -1, 1, 0),
        new("breathiness", "Breathiness", 0, 127, 0, 1, 0),
        new("air", "Air", 0, 127, 0, 1, 0),
    ];

    public static ProjectInfo Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("The VPR archive contains too many entries.");

        var sequenceEntries = archive.Entries
            .Where(entry => string.Equals(entry.FullName.Replace('\\', '/'), "Project/sequence.json", StringComparison.Ordinal))
            .ToArray();
        if (sequenceEntries.Length != 1)
            throw new InvalidDataException("The VPR archive must contain exactly one Project/sequence.json file.");

        var sequenceEntry = sequenceEntries[0];
        if (sequenceEntry.Length <= 0 || sequenceEntry.Length > MaxSequenceBytes)
            throw new InvalidDataException("The VPR sequence.json size is invalid or exceeds the import limit.");

        JObject sequence;
        using (var entryStream = sequenceEntry.Open())
        using (var limitedStream = new LimitedReadStream(entryStream, MaxSequenceBytes))
        using (var reader = new StreamReader(limitedStream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: false))
        using (var jsonReader = new JsonTextReader(reader) { MaxDepth = 128, DateParseHandling = DateParseHandling.None })
        {
            sequence = JObject.Load(jsonReader);
        }

        return ReadSequence(sequence);
    }

    public static void Serialize(Stream output, ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(project);

        var allParts = project.Tracks.SelectMany(track => track.Parts.OfType<MidiPartInfo>()).ToArray();
        JObject masterTrack = ReadSavedObject(allParts, RawMasterTrackProperty) ?? new JObject();
        JObject version = ReadSavedObject(allParts, RawVersionProperty) ?? new JObject();
        int major = allParts.Any(part => string.Equals(part.SoundSource.Type, "VOCALOID6", StringComparison.OrdinalIgnoreCase)) ? 6 : 5;
        version["major"] = major;

        ReplaceMasterEvents(masterTrack, project);
        var tracks = new JArray();
        foreach (var trackInfo in project.Tracks)
        {
            var parts = trackInfo.Parts.OfType<MidiPartInfo>().ToArray();
            if (parts.Length == 0)
                continue;

            JObject track = ReadSavedObject(parts, RawTrackProperty) ?? new JObject();
            track["name"] = trackInfo.Name;
            track["volume"] = new JObject { ["events"] = new JArray(new JObject { ["value"] = ToJsonNumber(trackInfo.Gain * 10) }) };
            track["panpot"] = new JObject { ["events"] = new JArray(new JObject { ["value"] = ToJsonNumber(trackInfo.Pan * 64) }) };
            track["isMuted"] = trackInfo.Mute;
            track["isSoloMode"] = trackInfo.Solo;

            var partArray = new JArray();
            foreach (var partInfo in parts)
                partArray.Add(WritePart(partInfo));
            track["parts"] = partArray;
            tracks.Add(track);
        }

        var sequence = new JObject
        {
            ["version"] = version,
            ["masterTrack"] = masterTrack,
            ["tracks"] = tracks,
        };

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var entry = archive.CreateEntry("Project/sequence.json", CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        using var jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.None, CloseOutput = false };
        sequence.WriteTo(jsonWriter);
        jsonWriter.Flush();
    }

    static ProjectInfo ReadSequence(JObject sequence)
    {
        var project = new ProjectInfo();
        ReadMasterTrack(sequence["masterTrack"] as JObject, project);

        string engineType = ReadEngineType(sequence["version"] as JObject);
        var tracks = sequence["tracks"] as JArray ?? [];
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            if (tracks[trackIndex] is not JObject track)
                continue;

            var trackInfo = new TrackInfo
            {
                Name = ReadString(track["name"], $"Track {trackIndex + 1}"),
                Gain = ReadTrackGain(track["volume"]),
                Pan = ReadTrackPan(track["panpot"]),
                Mute = ReadBool(track["isMuted"]),
                Solo = ReadBool(track["isSoloMode"]),
            };

            var parts = track["parts"] as JArray ?? [];
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                if (parts[partIndex] is not JObject part)
                    continue;

                var partInfo = new MidiPartInfo
                {
                    Name = ReadString(part["name"], $"Part {partIndex + 1}"),
                    Pos = ReadNumber(part["pos"]),
                    EndOffset = Math.Max(0, ReadNumber(part["duration"])),
                    SoundSource = new SoundSourceInfo
                    {
                        Type = engineType,
                        Id = ReadString(part["voice"]?["compID"]),
                    },
                    Properties = CreateProperties(
                        (RawPartProperty, part.ToString(Formatting.None)),
                        (RawTrackProperty, WithoutParts(track).ToString(Formatting.None)),
                        (RawMasterTrackProperty, (sequence["masterTrack"] as JObject ?? new JObject()).ToString(Formatting.None)),
                        (RawVersionProperty, (sequence["version"] as JObject ?? new JObject { ["major"] = 5 }).ToString(Formatting.None))),
                };

                ReadNotes(part["notes"] as JArray, partInfo);
                ReadControllers(part["controllers"] as JArray, partInfo);
                trackInfo.Parts.Add(partInfo);
            }

            project.Tracks.Add(trackInfo);
        }

        if (project.TimeSignatures.Count == 0)
            project.TimeSignatures.Add(new TimeSignatureInfo { BarIndex = 0, Numerator = 4, Denominator = 4 });

        return project;
    }

    static string ReadEngineType(JObject? version)
    {
        int major = ReadInt(version?["major"], 5);
        return major >= 6 ? "VOCALOID6" : "VOCALOID5";
    }

    static void ReadMasterTrack(JObject? masterTrack, ProjectInfo project)
    {
        var tempoEvents = masterTrack?["tempo"]?["events"] as JArray ?? [];
        foreach (var item in tempoEvents.OfType<JObject>())
        {
            double bpm = ReadNumber(item["value"]) / 100d;
            if (bpm <= 0 || !double.IsFinite(bpm))
                continue;
            project.Tempos.Add(new TempoInfo { Pos = ReadNumber(item["pos"]), Bpm = bpm });
        }

        project.Tempos.Sort((left, right) => left.Pos.CompareTo(right.Pos));

        var signatures = masterTrack?["timeSig"]?["events"] as JArray ?? [];
        foreach (var item in signatures.OfType<JObject>())
        {
            int numerator = ReadInt(item["numer"], 4);
            int denominator = ReadInt(item["denom"], 4);
            if (numerator <= 0 || denominator <= 0)
                continue;
            project.TimeSignatures.Add(new TimeSignatureInfo
            {
                BarIndex = Math.Max(0, ReadInt(item["bar"], 0)),
                Numerator = numerator,
                Denominator = denominator,
            });
        }

        project.TimeSignatures.Sort((left, right) => left.BarIndex.CompareTo(right.BarIndex));
    }

    static void ReadNotes(JArray? notes, MidiPartInfo part)
    {
        foreach (var note in notes?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            double duration = ReadNumber(note["duration"]);
            if (duration <= 0)
                continue;

            var properties = new Map<string, PropertyValue>();
            string phoneme = ReadString(note["phoneme"]);
            if (phoneme.Length > 0)
                properties.Add(PhonemeProperty, phoneme);
            properties.Add(RawNoteProperty, note.ToString(Formatting.None));

            part.Notes.Add(new NoteInfo
            {
                Pos = ReadNumber(note["pos"]),
                Dur = duration,
                Pitch = ReadInt(note["number"], 60),
                Lyric = ReadString(note["lyric"], "a"),
                Properties = new PropertyObject(properties),
            });
        }
    }

    static void ReadControllers(JArray? controllers, MidiPartInfo part)
    {
        foreach (var definition in ControllerDefinitions)
        {
            var controller = controllers?.OfType<JObject>()
                .FirstOrDefault(item => string.Equals(ReadString(item["name"]), definition.Source, StringComparison.Ordinal));
            var events = controller?["events"] as JArray;
            if (events == null)
                continue;

            var points = events.OfType<JObject>()
                .Select(item => (Pos: ReadNumber(item["pos"]), Value: ReadNumber(item["value"], double.NaN)))
                .Where(point => double.IsFinite(point.Pos) && double.IsFinite(point.Value))
                .OrderBy(point => point.Pos)
                .GroupBy(point => point.Pos)
                .Select(group => group.Last())
                .ToArray();
            if (points.Length == 0)
                continue;

            var automation = new AutomationInfo { DefaultValue = definition.Default };
            if (points[0].Pos > 0)
            {
                automation.Points.Add(new Point(0, definition.Default));
                if (points[0].Pos > 1)
                    automation.Points.Add(new Point(points[0].Pos - 1, definition.Default));
            }

            foreach (var point in points)
            {
                double value = RangeMap(point.Value, definition.Min, definition.Max, definition.OutMin, definition.OutMax);
                if (automation.Points.Count > 0 && point.Pos - 1 > automation.Points[^1].X)
                    automation.Points.Add(new Point(point.Pos - 1, RangeMap(points[Math.Max(0, Array.IndexOf(points, point) - 1)].Value,
                        definition.Min, definition.Max, definition.OutMin, definition.OutMax)));
                automation.Points.Add(new Point(point.Pos, value));
            }

            part.Automations[definition.Target] = automation;
        }
    }

    static JObject WithoutParts(JObject track)
    {
        var clone = (JObject)track.DeepClone();
        clone.Remove("parts");
        return clone;
    }

    static JObject? ReadSavedObject(IEnumerable<MidiPartInfo> parts, string propertyName)
    {
        foreach (var part in parts)
        {
            string json = part.Properties.GetString(propertyName);
            if (string.IsNullOrWhiteSpace(json))
                continue;
            try
            {
                if (JObject.Parse(json) is { } value)
                    return value;
            }
            catch (JsonException)
            {
                // Malformed or user-edited private metadata is ignored; supported fields are rebuilt below.
            }
        }
        return null;
    }

    static void ReplaceMasterEvents(JObject masterTrack, ProjectInfo project)
    {
        var tempo = masterTrack["tempo"] as JObject ?? new JObject();
        tempo["events"] = new JArray(project.Tempos
            .Where(item => double.IsFinite(item.Pos) && double.IsFinite(item.Bpm) && item.Bpm > 0)
            .OrderBy(item => item.Pos)
            .Select(item => new JObject
            {
                ["pos"] = ToJsonNumber(item.Pos),
                ["value"] = ToJsonNumber(item.Bpm * 100),
            }));
        masterTrack["tempo"] = tempo;

        var timeSig = masterTrack["timeSig"] as JObject ?? new JObject();
        timeSig["events"] = new JArray(project.TimeSignatures
            .Where(item => item.BarIndex >= 0 && item.Numerator > 0 && item.Denominator > 0)
            .OrderBy(item => item.BarIndex)
            .Select(item => new JObject
            {
                ["bar"] = item.BarIndex,
                ["numer"] = item.Numerator,
                ["denom"] = item.Denominator,
            }));
        masterTrack["timeSig"] = timeSig;
    }

    static JObject WritePart(MidiPartInfo partInfo)
    {
        JObject part = ReadSavedObject([partInfo], RawPartProperty) ?? new JObject();
        part["name"] = partInfo.Name;
        part["pos"] = ToJsonNumber(partInfo.Pos);
        part["duration"] = ToJsonNumber(Math.Max(0, partInfo.EndOffset));

        var voice = part["voice"] as JObject ?? new JObject();
        voice["compID"] = partInfo.SoundSource.Id;
        part["voice"] = voice;

        var notes = new JArray();
        foreach (var noteInfo in partInfo.Notes.Where(note => double.IsFinite(note.Pos) && double.IsFinite(note.Dur) && note.Dur > 0))
        {
            JObject note = ReadSavedObject([new MidiPartInfo { Properties = noteInfo.Properties }], RawNoteProperty) ?? new JObject();
            note["pos"] = ToJsonNumber(noteInfo.Pos);
            note["duration"] = ToJsonNumber(noteInfo.Dur);
            note["number"] = noteInfo.Pitch;
            note["lyric"] = noteInfo.Lyric;
            string phoneme = noteInfo.Properties.GetString(PhonemeProperty);
            if (phoneme.Length > 0)
                note["phoneme"] = phoneme;
            else
                note.Remove("phoneme");
            notes.Add(note);
        }
        part["notes"] = notes;

        var controllers = part["controllers"] as JArray ?? new JArray();
        foreach (var definition in ControllerDefinitions)
        {
            partInfo.Automations.TryGetValue(definition.Target, out var automation);
            var old = controllers.OfType<JObject>()
                .FirstOrDefault(item => string.Equals(ReadString(item["name"]), definition.Source, StringComparison.Ordinal));
            if (automation == null)
            {
                foreach (var duplicate in controllers.OfType<JObject>()
                             .Where(item => string.Equals(ReadString(item["name"]), definition.Source, StringComparison.Ordinal)).ToArray())
                    duplicate.Remove();
                continue;
            }

            JObject controller = old == null ? new JObject { ["name"] = definition.Source } : (JObject)old.DeepClone();
            controller["events"] = new JArray(automation.Points
                .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
                .OrderBy(point => point.X)
                .Select(point => new JObject
                {
                    ["pos"] = ToJsonNumber(point.X),
                    ["value"] = ToJsonNumber(ToSourceValue(point.Y, definition)),
                }));
            if (old == null)
                controllers.Add(controller);
            else
            {
                int index = controllers.IndexOf(old);
                controllers[index] = controller;
            }
        }
        part["controllers"] = controllers;
        return part;
    }

    static double ToSourceValue(double value, ControllerDefinition definition)
    {
        if (definition.OutMax == definition.OutMin)
            return definition.Min;
        double normalized = (Math.Clamp(value, Math.Min(definition.OutMin, definition.OutMax), Math.Max(definition.OutMin, definition.OutMax)) - definition.OutMin)
            / (definition.OutMax - definition.OutMin);
        return Math.Round(definition.Min + normalized * (definition.Max - definition.Min), MidpointRounding.AwayFromZero);
    }

    static JToken ToJsonNumber(double value)
        => double.IsFinite(value) ? new JValue(value) : JValue.CreateNull();

    sealed record ControllerDefinition(string Source, string Target, double Min, double Max, double OutMin, double OutMax, double Default);

    static double ReadTrackGain(JToken? volume)
    {
        var events = volume?["events"] as JArray;
        if (events == null || events.Count == 0)
            return 0;
        double db = ReadNumber(events[0]?["value"]) / 10d;
        return Math.Clamp(db, -24, 6);
    }

    static double ReadTrackPan(JToken? panpot)
    {
        var events = panpot?["events"] as JArray;
        if (events == null || events.Count == 0)
            return 0;
        return Math.Clamp(ReadNumber(events[0]?["value"]) / 64d, -1, 1);
    }

    static PropertyObject CreateProperties(params (string Key, string Value)[] values)
    {
        var map = new Map<string, PropertyValue>();
        foreach (var (key, value) in values)
            map.Add(key, value);
        return new PropertyObject(map);
    }

    static double RangeMap(double value, double min, double max, double outMin, double outMax)
    {
        if (max <= min)
            return outMin;
        return outMin + (Math.Clamp(value, min, max) - min) * (outMax - outMin) / (max - min);
    }

    static string ReadString(JToken? token, string fallback = "")
        => token?.Type == JTokenType.String ? (string?)token ?? fallback : fallback;

    static bool ReadBool(JToken? token)
        => token?.Type == JTokenType.Boolean && (bool)token;

    static int ReadInt(JToken? token, int fallback = 0)
        => int.TryParse(token?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    static double ReadNumber(JToken? token, double fallback = 0)
        => double.TryParse(token?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : fallback;

    sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        long mRead;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, (int)Math.Min(count, limit - mRead + 1));
            mRead += read;
            if (mRead > limit)
                throw new InvalidDataException("The VPR sequence.json exceeds the import limit.");
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - mRead + 1)]);
            mRead += read;
            if (mRead > limit)
                throw new InvalidDataException("The VPR sequence.json exceeds the import limit.");
            return read;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
