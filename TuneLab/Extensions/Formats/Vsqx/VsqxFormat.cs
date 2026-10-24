using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using TuneLab.Extensions.Formats;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace TuneLab.Extensions.Formats.Vsqx;

internal sealed class VsqxFormat : IImportFormat, IExportFormat, ISourceFileImportFormat
{
    public ProjectInfo Deserialize(Stream stream) => VsqxUtility.Deserialize(stream);

    public ProjectInfo Deserialize(Stream stream, string sourceFilePath)
        => VsqxUtility.Deserialize(stream, sourceFilePath);

    public void Serialize(Stream output, ProjectInfo info) => VsqxUtility.Serialize(output, info);
}

internal static class VsqxUtility
{
    const string VoiceBankProperty = "vsqx.voiceBank";
    const string VoiceProgramProperty = "vsqx.voiceProgram";
    const string VoiceNameProperty = "vsqx.voiceName";
    const string PartCommentProperty = "vsqx.comment";
    const string SingerXmlProperty = "vsqx.singerXml";
    const string SingerPluginXmlProperty = "vsqx.sPlugXml";
    const string PartStyleXmlProperty = "vsqx.pStyleXml";
    const string NoteStyleXmlProperty = "vsqx.nStyleXml";
    const string VelocityProperty = "vsqx.velocity";
    const string MasterAuxXmlProperty = "vsqx.masterXml";
    const string MixerXmlProperty = "vsqx.mixerXml";
    const string VoiceTableXmlProperty = "vsqx.voiceTableXml";
    const string RootAuxXmlProperty = "vsqx.auxXml";
    const string Vsq4Namespace = "http://www.yamaha.co.jp/vocaloid/schema/vsq4/";
    const string Vsq4SchemaLocation = "http://www.yamaha.co.jp/vocaloid/schema/vsq4/ vsq4.xsd";

    public static ProjectInfo Deserialize(Stream stream, string? sourceFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // VSQX 是 XML，但不需要 DTD 或外部实体。显式关闭它们，避免打开用户文件时读取本地文件或访问网络。
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root == null || LocalName(root) is not ("vsq4" or "vsq3"))
            throw new InvalidDataException("The file is not a supported VOCALOID VSQX project.");

        var masterTrack = Child(root, "masterTrack");
        int resolution = ReadInt(Child(masterTrack, "resolution"), 480);
        if (resolution <= 0)
            throw new InvalidDataException("The VSQX time resolution must be greater than zero.");

        double ScaleTick(double value) => value * MusicTheory.RESOLUTION / resolution;
        double ReadTick(XElement? element, double fallback = 0)
            => ScaleTick(ReadDouble(element, fallback));

        var project = new ProjectInfo();
        ReadTempoMap(masterTrack, project, ScaleTick);
        ReadTimeSignatures(masterTrack, project);

        var voiceNames = ReadVoiceNames(Child(root, "vVoiceTable"));
        var vsTracks = Children(root, "vsTrack").ToArray();
        for (int trackIndex = 0; trackIndex < vsTracks.Length; trackIndex++)
        {
            var vsTrack = vsTracks[trackIndex];
            var trackInfo = new TrackInfo
            {
                Name = ReadText(Child(vsTrack, "name"), $"VOCALOID Track {trackIndex + 1}"),
            };

            var vsParts = Children(vsTrack, "vsPart").ToArray();
            for (int partIndex = 0; partIndex < vsParts.Length; partIndex++)
            {
                var vsPart = vsParts[partIndex];
                var partInfo = new MidiPartInfo
                {
                    Name = ReadText(Child(vsPart, "name"), $"Part {partIndex + 1}"),
                    Pos = ReadTick(Child(vsPart, "t")),
                    EndOffset = Math.Max(0, ReadTick(Child(vsPart, "playTime"))),
                    Properties = ReadPartProperties(vsPart, root, partIndex == 0, voiceNames),
                };

                foreach (var noteElement in Children(vsPart, "note"))
                {
                    double duration = ReadTick(Child(noteElement, "dur"));
                    if (duration <= 0)
                        continue;

                    var note = new NoteInfo
                    {
                        Pos = ReadTick(Child(noteElement, "t")),
                        Dur = duration,
                        Pitch = ReadInt(Child(noteElement, "n"), 60),
                        Lyric = ReadText(Child(noteElement, "y"), "a"),
                        Pronunciation = ReadText(Child(noteElement, "p")),
                        Properties = ReadNoteProperties(noteElement),
                    };
                    partInfo.Notes.Add(note);
                }

                ReadAutomations(vsPart, partInfo, ScaleTick);
                trackInfo.Parts.Add(partInfo);
            }

            project.Tracks.Add(trackInfo);
        }

        ReadAudioTracks(root, project, ScaleTick, sourceFilePath);
        return project;
    }

    public static void Serialize(Stream output, ProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(project);

        XNamespace ns = Vsq4Namespace;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var midiTracks = project.Tracks
            .Select(track => (Track: track, Parts: track.Parts.OfType<MidiPartInfo>().ToArray()))
            .Where(item => item.Parts.Length > 0)
            .ToArray();
        var allMidiParts = midiTracks.SelectMany(item => item.Parts).ToArray();
        XElement? savedMaster = FindSavedElement(allMidiParts, MasterAuxXmlProperty, "masterTrack");
        XElement? savedMixer = FindSavedElement(allMidiParts, MixerXmlProperty, "mixer");
        XElement? savedVoiceTable = FindSavedElement(allMidiParts, VoiceTableXmlProperty, "vVoiceTable");
        XElement? savedAux = FindSavedElement(allMidiParts, RootAuxXmlProperty, "auxiliaryData");

        var root = new XElement(ns + "vsq4",
            new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
            new XAttribute(xsi + "schemaLocation", Vsq4SchemaLocation),
            new XElement(ns + "vender", "Yamaha corporation"),
            new XElement(ns + "version", "4.0.0.3"),
            savedVoiceTable == null ? new XElement(ns + "vVoiceTable") : CopyInNamespace(savedVoiceTable, ns),
            savedMixer == null ? new XElement(ns + "mixer") : CopyInNamespace(savedMixer, ns),
            BuildMasterTrack(ns, project, savedMaster));

        for (int trackIndex = 0; trackIndex < midiTracks.Length; trackIndex++)
            root.Add(BuildVoiceTrack(ns, midiTracks[trackIndex].Track, midiTracks[trackIndex].Parts, trackIndex));

        foreach (var (trackName, elementName) in new[]
        {
            ("VOCALOID Mono Audio", "monoTrack"),
            ("VOCALOID Stereo Audio", "stTrack"),
        })
        {
            var audioParts = project.Tracks
                .Where(track => string.Equals(track.Name, trackName, StringComparison.Ordinal))
                .SelectMany(track => track.Parts.OfType<AudioPartInfo>())
                .Where(part => !string.IsNullOrWhiteSpace(part.Path)).ToArray();
            if (audioParts.Length == 0)
                continue;

            var audioTrack = new XElement(ns + elementName);
            foreach (var part in audioParts)
            {
                audioTrack.Add(new XElement(ns + "wavPart",
                    new XElement(ns + "t", FormatTick(part.Pos)),
                    new XElement(ns + "playTime", FormatTick(Math.Max(0, part.EndOffset))),
                    new XElement(ns + "name", part.Name),
                    new XElement(ns + "filePath", part.Path)));
            }
            root.Add(audioTrack);
        }

        if (savedAux != null)
        {
            foreach (var aux in Children(savedAux, "aux"))
                root.Add(CopyInNamespace(aux, ns));
        }

        var settings = new XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            CloseOutput = false,
        };
        using var writer = XmlWriter.Create(output, settings);
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
    }

    static XElement BuildMasterTrack(XNamespace ns, ProjectInfo project, XElement? savedMaster)
    {
        var master = savedMaster == null ? new XElement(ns + "masterTrack") : CopyInNamespace(savedMaster, ns);
        var oldResolution = Child(master, "resolution");
        XElement resolution;
        if (oldResolution != null)
        {
            oldResolution.Value = MusicTheory.RESOLUTION.ToString(CultureInfo.InvariantCulture);
            resolution = oldResolution;
        }
        else
        {
            resolution = new XElement(ns + "resolution", MusicTheory.RESOLUTION);
            var beforeResolution = Child(master, "comment") ?? Child(master, "seqName");
            if (beforeResolution == null)
                master.AddFirst(resolution);
            else
                beforeResolution.AddAfterSelf(resolution);
        }

        foreach (var child in Children(master, "tempo").Concat(Children(master, "timeSig")).ToArray())
            child.Remove();

        XElement anchor = Child(master, "preMeasure") ?? resolution;
        var signatures = project.TimeSignatures.OrderBy(signature => signature.BarIndex).ToArray();
        foreach (var signature in signatures)
        {
            anchor.AddAfterSelf(new XElement(ns + "timeSig",
                new XElement(ns + "m", Math.Max(0, signature.BarIndex)),
                new XElement(ns + "nu", Math.Max(1, signature.Numerator)),
                new XElement(ns + "de", Math.Max(1, signature.Denominator))));
            anchor = anchor.ElementsAfterSelf().First();
        }

        var tempoAnchor = anchor;
        foreach (var tempo in project.Tempos.OrderBy(tempo => tempo.Pos))
        {
            if (!double.IsFinite(tempo.Bpm) || tempo.Bpm <= 0)
                throw new InvalidDataException("VSQX tempo values must be finite and greater than zero.");
            tempoAnchor.AddAfterSelf(new XElement(ns + "tempo",
                new XElement(ns + "t", FormatTick(tempo.Pos)),
                new XElement(ns + "v", (int)Math.Round(Math.Clamp(tempo.Bpm, 0.01, 999.99) * 100))));
            tempoAnchor = tempoAnchor.ElementsAfterSelf().First();
        }
        return master;
    }

    static XElement BuildVoiceTrack(XNamespace ns, TrackInfo track, IReadOnlyList<MidiPartInfo> parts, int trackIndex)
    {
        var element = new XElement(ns + "vsTrack",
            new XElement(ns + "tNo", trackIndex),
            new XElement(ns + "name", track.Name),
            new XElement(ns + "comment", track.Name));
        foreach (var part in parts)
            element.Add(BuildVoicePart(ns, part));
        return element;
    }

    static XElement BuildVoicePart(XNamespace ns, MidiPartInfo part)
    {
        var element = new XElement(ns + "vsPart",
            new XElement(ns + "t", FormatTick(part.Pos)),
            new XElement(ns + "playTime", FormatTick(Math.Max(0, part.EndOffset))),
            new XElement(ns + "name", part.Name));
        AddSavedChild(element, ns, part.Properties, PartCommentProperty, "comment");
        AddSavedChild(element, ns, part.Properties, SingerPluginXmlProperty, "sPlug");
        AddSavedChild(element, ns, part.Properties, PartStyleXmlProperty, "pStyle");
        if (!TryAddSavedChild(element, ns, part.Properties, SingerXmlProperty, "singer"))
            element.Add(new XElement(ns + "singer", new XElement(ns + "t", 0), new XElement(ns + "bs", 0), new XElement(ns + "pc", 0)));

        foreach (var controller in BuildControllers(ns, part))
            element.Add(controller);
        foreach (var note in part.Notes.OrderBy(note => note.Pos))
        {
            if (note.Dur <= 0)
                continue;
            var noteElement = new XElement(ns + "note",
                new XElement(ns + "t", FormatTick(note.Pos)),
                new XElement(ns + "dur", FormatTick(note.Dur)),
                new XElement(ns + "n", Math.Clamp(note.Pitch, 0, 127)),
                new XElement(ns + "v", Math.Clamp((int)Math.Round(note.Properties.GetDouble(VelocityProperty, 64)), 0, 127)),
                new XElement(ns + "y", note.Lyric),
                new XElement(ns + "p", note.Pronunciation));
            AddSavedChild(noteElement, ns, note.Properties, NoteStyleXmlProperty, "nStyle");
            element.Add(noteElement);
        }
        return element;
    }

    static IEnumerable<XElement> BuildControllers(XNamespace ns, MidiPartInfo part)
    {
        var controls = new Dictionary<long, Dictionary<string, int>>();
        AddController("D", part.Automations.TryGetValue("Dynamics", out var dynamics)
            ? dynamics
            : VolumeAsDynamics(part.Automations.TryGetValue(TuneLab.ConstantDefine.VolumeID, out var volume) ? volume : null));
        AddController("P", part.Automations.TryGetValue("PitchBend", out var pitch) ? pitch : null, 0);
        AddController("S", part.Automations.TryGetValue("PitchBendSensitivity", out var sensitivity) ? sensitivity : null, 12);

        foreach (var (position, values) in controls.OrderBy(pair => pair.Key))
        {
            var cc = new XElement(ns + "cc", new XElement(ns + "t", position));
            foreach (var value in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                cc.Add(new XElement(ns + "v", new XAttribute("id", value.Key), value.Value));
            yield return cc;
        }

        void AddController(string id, AutomationInfo? automation, double fallback = 0)
        {
            var values = new SortedDictionary<long, double>();
            values[0] = automation?.DefaultValue ?? fallback;
            if (automation != null)
                foreach (var point in automation.Points)
                    values[FormatTick(point.X)] = point.Y;

            foreach (var (position, value) in values)
            {
                if (!controls.TryGetValue(position, out var atPosition))
                {
                    atPosition = new Dictionary<string, int>(StringComparer.Ordinal);
                    controls.Add(position, atPosition);
                }
                int rounded = (int)Math.Round(double.IsFinite(value) ? value : fallback);
                atPosition[id] = id switch
                {
                    "D" => Math.Clamp(rounded, 0, 127),
                    "P" => Math.Clamp(rounded, -8192, 8191),
                    "S" => Math.Clamp(rounded, 0, 24),
                    _ => rounded,
                };
            }
        }
    }

    static AutomationInfo? VolumeAsDynamics(AutomationInfo? volume)
    {
        if (volume == null)
            return null;
        return new AutomationInfo
        {
            DefaultValue = Math.Clamp(64 + volume.DefaultValue * (64d / 12), 0, 127),
            Points = volume.Points.Select(point => new Point(point.X, Math.Clamp(64 + point.Y * (64d / 12), 0, 127))).ToList(),
        };
    }

    static XElement? FindSavedElement(IEnumerable<MidiPartInfo> parts, string key, string elementName)
    {
        foreach (var part in parts)
        {
            var element = ParseElement(part.Properties.GetString(key));
            if (element != null && LocalName(element) == elementName)
                return element;
        }
        return null;
    }

    static void AddSavedChild(XElement target, XNamespace ns, PropertyObject properties, string key, string elementName)
        => TryAddSavedChild(target, ns, properties, key, elementName);

    static bool TryAddSavedChild(XElement target, XNamespace ns, PropertyObject properties, string key, string elementName)
    {
        var element = ParseElement(properties.GetString(key));
        if (element == null || LocalName(element) != elementName)
            return false;
        target.Add(CopyInNamespace(element, ns));
        return true;
    }

    static XElement? ParseElement(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            return XElement.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    static XElement CopyInNamespace(XElement element, XNamespace ns)
    {
        var copy = new XElement(ns + LocalName(element),
            element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration),
            element.Nodes().Select(node => node is XElement child ? CopyInNamespace(child, ns) : CloneNode(node)));
        return copy;
    }

    static XNode CloneNode(XNode node) => node switch
    {
        XCData cdata => new XCData(cdata.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        XProcessingInstruction instruction => new XProcessingInstruction(instruction.Target, instruction.Data),
        _ => new XText(node.ToString()),
    };

    static long FormatTick(double tick)
    {
        if (!double.IsFinite(tick) || tick >= long.MaxValue || tick <= long.MinValue)
            throw new InvalidDataException("VSQX positions and durations must be finite values within the supported range.");
        return (long)Math.Round(tick);
    }

    static void ReadTempoMap(XElement? masterTrack, ProjectInfo project, Func<double, double> scaleTick)
    {
        foreach (var tempo in Children(masterTrack, "tempo"))
        {
            double bpm = ReadDouble(Child(tempo, "v"), 12000) / 100d;
            if (bpm <= 0)
                continue;

            project.Tempos.Add(new TempoInfo
            {
                Pos = scaleTick(ReadDouble(Child(tempo, "t"))),
                Bpm = bpm,
            });
        }

        if (project.Tempos.Count == 0)
            project.Tempos.Add(new TempoInfo { Pos = 0, Bpm = 120 });
        else
            project.Tempos.Sort((left, right) => left.Pos.CompareTo(right.Pos));
    }

    static void ReadTimeSignatures(XElement? masterTrack, ProjectInfo project)
    {
        foreach (var signature in Children(masterTrack, "timeSig"))
        {
            int numerator = ReadInt(Child(signature, "nu"), 4);
            int denominator = ReadInt(Child(signature, "de"), 4);
            if (numerator <= 0 || denominator <= 0)
                continue;

            project.TimeSignatures.Add(new TimeSignatureInfo
            {
                BarIndex = Math.Max(0, ReadInt(Child(signature, "m"), 0)),
                Numerator = numerator,
                Denominator = denominator,
            });
        }

        if (project.TimeSignatures.Count == 0)
            project.TimeSignatures.Add(new TimeSignatureInfo { BarIndex = 0, Numerator = 4, Denominator = 4 });
        else
            project.TimeSignatures.Sort((left, right) => left.BarIndex.CompareTo(right.BarIndex));
    }

    static Dictionary<(int Bank, int Program), string> ReadVoiceNames(XElement? voiceTable)
    {
        var result = new Dictionary<(int, int), string>();
        foreach (var voice in Children(voiceTable, "vVoice"))
        {
            int bank = ReadInt(Child(voice, "bs"), -1);
            int program = ReadInt(Child(voice, "pc"), -1);
            var name = ReadText(Child(voice, "name"));
            if (bank >= 0 && program >= 0 && name.Length > 0)
                result[(bank, program)] = name;
        }
        return result;
    }

    static PropertyObject ReadPartProperties(
        XElement part,
        XElement root,
        bool firstPartOnTrack,
        IReadOnlyDictionary<(int Bank, int Program), string> voiceNames)
    {
        var values = new Map<string, PropertyValue>();
        AddXml(values, PartCommentProperty, Child(part, "comment"));
        AddXml(values, SingerXmlProperty, Child(part, "singer"));
        AddXml(values, SingerPluginXmlProperty, Child(part, "sPlug"));
        AddXml(values, PartStyleXmlProperty, Child(part, "pStyle"));

        var singer = Child(part, "singer");
        int bank = ReadInt(Child(singer, "bs"), -1);
        int program = ReadInt(Child(singer, "pc"), -1);
        if (bank >= 0)
            values.Add(VoiceBankProperty, (double)bank);
        if (program >= 0)
            values.Add(VoiceProgramProperty, (double)program);
        if (voiceNames.TryGetValue((bank, program), out var voiceName))
            values.Add(VoiceNameProperty, voiceName);

        // ProjectInfo has no project-level extension-data slot. Keep the original global VSQX blocks on the
        // first part so they survive a native TuneLab save and can be reused by a future VSQX exporter.
        if (firstPartOnTrack)
        {
            AddXml(values, MasterAuxXmlProperty, Child(root, "masterTrack"));
            AddXml(values, MixerXmlProperty, Child(root, "mixer"));
            AddXml(values, VoiceTableXmlProperty, Child(root, "vVoiceTable"));
            AddXml(values, RootAuxXmlProperty, Children(root, "aux").Any()
                ? new XElement("auxiliaryData", Children(root, "aux"))
                : null);
        }

        return new PropertyObject(values);
    }

    static PropertyObject ReadNoteProperties(XElement note)
    {
        var values = new Map<string, PropertyValue>();
        var velocity = Child(note, "v");
        if (velocity != null && double.TryParse(velocity.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            values.Add(VelocityProperty, value);
        AddXml(values, NoteStyleXmlProperty, Child(note, "nStyle"));
        return new PropertyObject(values);
    }

    static void ReadAutomations(XElement part, MidiPartInfo partInfo, Func<double, double> scaleTick)
    {
        var defaults = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["D"] = 64,
            ["P"] = 0,
            ["S"] = 12,
        };
        var ids = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["D"] = "Dynamics",
            ["P"] = "PitchBend",
            ["S"] = "PitchBendSensitivity",
        };
        var pointsByParameter = new Dictionary<string, List<Point>>(StringComparer.Ordinal);

        foreach (var controller in Children(part, "cc"))
        {
            double position = scaleTick(ReadDouble(Child(controller, "t")));
            foreach (var value in Children(controller, "v"))
            {
                string? sourceId = value.Attribute("id")?.Value;
                if (sourceId == null || !ids.ContainsKey(sourceId))
                    continue;

                if (!double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericValue))
                    continue;
                if (!pointsByParameter.TryGetValue(sourceId, out var points))
                {
                    points = [];
                    pointsByParameter.Add(sourceId, points);
                }
                points.Add(new Point(position, numericValue));
            }
        }

        foreach (var (sourceId, points) in pointsByParameter)
        {
            points.Sort((left, right) => left.X.CompareTo(right.X));
            var automation = new AutomationInfo
            {
                DefaultValue = defaults[sourceId],
                Points = points,
            };
            partInfo.Automations.Add(ids[sourceId], automation);

            // TuneLab's part Volume lane is always editable and applies after voice synthesis. Keep the native
            // Dynamics values above for a future VOCALOID engine, while exposing a useful amplitude approximation
            // in the current editor so the imported curve is visible and survives playback/export.
            if (sourceId == "D")
            {
                partInfo.Automations.Add(TuneLab.ConstantDefine.VolumeID, new AutomationInfo
                {
                    DefaultValue = MapDynamicsToDb(automation.DefaultValue),
                    Points = points.Select(point => new Point(point.X, MapDynamicsToDb(point.Y))).ToList(),
                });
            }
        }
    }

    static double MapDynamicsToDb(double value) => Math.Clamp((value - 64) * (12d / 64), -12, 12);

    static void ReadAudioTracks(
        XElement root,
        ProjectInfo project,
        Func<double, double> scaleTick,
        string? sourceFilePath)
    {
        AddAudioTrack("monoTrack", "VOCALOID Mono Audio", project, root, scaleTick, sourceFilePath);
        AddAudioTrack("stTrack", "VOCALOID Stereo Audio", project, root, scaleTick, sourceFilePath);
    }

    static void AddAudioTrack(
        string elementName,
        string defaultTrackName,
        ProjectInfo project,
        XElement root,
        Func<double, double> scaleTick,
        string? sourceFilePath)
    {
        var waveParts = Children(Child(root, elementName), "wavPart").ToArray();
        if (waveParts.Length == 0)
            return;

        var track = new TrackInfo { Name = defaultTrackName };
        for (int i = 0; i < waveParts.Length; i++)
        {
            var wavePart = waveParts[i];
            track.Parts.Add(new AudioPartInfo
            {
                Name = ReadText(Child(wavePart, "name"), $"Audio {i + 1}"),
                Pos = scaleTick(ReadDouble(Child(wavePart, "t"))),
                EndOffset = Math.Max(0, scaleTick(ReadDouble(Child(wavePart, "playTime")))),
                Path = ResolveAudioPath(ReadText(Child(wavePart, "filePath")), sourceFilePath),
            });
        }
        project.Tracks.Add(track);
    }

    static string ResolveAudioPath(string path, string? sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sourceFilePath) || File.Exists(path))
            return path;

        string normalizedPath = path.Replace('\\', '/');
        string? sourceDirectory = System.IO.Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(sourceDirectory))
            return path;

        bool hasWindowsDrive = normalizedPath.Length >= 3
            && char.IsAsciiLetter(normalizedPath[0])
            && normalizedPath[1] == ':'
            && normalizedPath[2] == '/';
        if (!System.IO.Path.IsPathRooted(normalizedPath) && !hasWindowsDrive)
        {
            string relativePath = System.IO.Path.Combine(sourceDirectory, normalizedPath);
            if (File.Exists(relativePath))
                return relativePath;
        }

        string fileName = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
        if (fileName.Length == 0)
            return path;

        string siblingPath = System.IO.Path.Combine(sourceDirectory, fileName);
        return File.Exists(siblingPath) ? siblingPath : path;
    }

    static void AddXml(Map<string, PropertyValue> values, string key, XElement? element)
    {
        if (element != null)
            values.Add(key, element.ToString(SaveOptions.DisableFormatting));
    }

    static string LocalName(XElement element) => element.Name.LocalName;

    static XElement? Child(XElement? parent, string name)
        => parent?.Elements().FirstOrDefault(element => LocalName(element) == name);

    static IEnumerable<XElement> Children(XElement? parent, string name)
        => parent?.Elements().Where(element => LocalName(element) == name) ?? Enumerable.Empty<XElement>();

    static string ReadText(XElement? element, string fallback = "") => element?.Value ?? fallback;

    static int ReadInt(XElement? element, int fallback)
        => int.TryParse(element?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    static double ReadDouble(XElement? element, double fallback = 0)
        => double.TryParse(element?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
