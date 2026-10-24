using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using TuneLab.Extensions.Formats.Vpr;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

public class VprImportTests
{
    [Fact]
    public void Deserialize_ImportsVocaloid6ProjectContent()
    {
        using var stream = CreateArchive(SampleV6);

        var project = VprUtility.Deserialize(stream);

        Assert.Equal([0, 960], project.Tempos.Select(tempo => tempo.Pos));
        Assert.Equal([120.0, 88.5], project.Tempos.Select(tempo => tempo.Bpm));
        Assert.Equal((2, 3, 4), (Assert.Single(project.TimeSignatures).BarIndex,
            project.TimeSignatures[0].Numerator, project.TimeSignatures[0].Denominator));
        var track = Assert.Single(project.Tracks);
        Assert.Equal("Lead", track.Name);
        Assert.Equal(-12, track.Gain);
        Assert.Equal(-0.5, track.Pan);
        Assert.True(track.Mute);
        var part = Assert.IsType<MidiPartInfo>(Assert.Single(track.Parts));
        Assert.Equal(240, part.Pos);
        Assert.Equal(1920, part.EndOffset);
        Assert.Equal("VOCALOID6", part.SoundSource.Type);
        Assert.Equal("voice-id", part.SoundSource.Id);
        var note = Assert.Single(part.Notes);
        Assert.Equal((120, 360, 67, "hello"), (note.Pos, note.Dur, note.Pitch, note.Lyric));
        Assert.Equal("h e l o", note.Properties.GetString("vpr.phoneme"));
        Assert.Contains("phoneme", note.Properties.GetString("vpr.rawNote"));
        Assert.Contains("midiEffects", part.Properties.GetString("vpr.rawPart"));
        Assert.Equal(-1, part.Automations["Dynamics"].Points[0].Y);
        Assert.Equal(1, part.Automations["Dynamics"].Points[^1].Y);
        Assert.Equal(-1, part.Automations["PitchBend"].Points[0].Y);
        Assert.Equal(1, part.Automations["PitchBend"].Points[^1].Y);
    }

    [Fact]
    public void Deserialize_UsesVocaloid5ForOlderAndMissingVersionMetadata()
    {
        using var stream = CreateArchive(SampleV5);

        var project = VprUtility.Deserialize(stream);

        var part = Assert.IsType<MidiPartInfo>(Assert.Single(Assert.Single(project.Tracks).Parts));
        Assert.Equal("VOCALOID5", part.SoundSource.Type);
        Assert.Equal("old-voice", part.SoundSource.Id);
    }

    [Fact]
    public void Serialize_RoundTripsEditableContentAndPreservesUnknownVprFields()
    {
        using var input = CreateArchive(SampleV6);
        var project = VprUtility.Deserialize(input);
        var track = Assert.Single(project.Tracks);
        var part = Assert.IsType<MidiPartInfo>(Assert.Single(track.Parts));
        var note = Assert.Single(part.Notes);
        note.Lyric = "updated";
        note.Pitch = 72;
        part.Automations["Dynamics"].Points[^1] = new TuneLab.Foundation.Point(120, 0.5);

        using var output = new MemoryStream();
        VprUtility.Serialize(output, project);
        output.Position = 0;

        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("Project/sequence.json", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        var sequence = JObject.Parse(reader.ReadToEnd());
        Assert.Equal(6, (int)sequence["version"]!["major"]!);
        Assert.Equal("master-extra", (string?)sequence["masterTrack"]!["customMaster"]);
        Assert.Equal("track-extra", (string?)sequence["tracks"]![0]!["customTrack"]);
        Assert.Equal("part-extra", (string?)sequence["tracks"]![0]!["parts"]![0]!["customPart"]);
        Assert.Equal("note-extra", (string?)sequence["tracks"]![0]!["parts"]![0]!["notes"]![0]!["customNote"]);
        Assert.Equal("updated", (string?)sequence["tracks"]![0]!["parts"]![0]!["notes"]![0]!["lyric"]);
        Assert.Equal(72, (int)sequence["tracks"]![0]!["parts"]![0]!["notes"]![0]!["number"]!);

        output.Position = 0;
        var reimported = VprUtility.Deserialize(output);
        var roundTripPart = Assert.IsType<MidiPartInfo>(Assert.Single(Assert.Single(reimported.Tracks).Parts));
        var roundTripNote = Assert.Single(roundTripPart.Notes);
        Assert.Equal("updated", roundTripNote.Lyric);
        Assert.Equal(72, roundTripNote.Pitch);
        Assert.Equal(0.5, roundTripPart.Automations["Dynamics"].Points[^1].Y, precision: 2);
    }

    [Fact]
    public void Deserialize_RejectsArchivesWithoutSequenceOrWithDuplicateSequences()
    {
        using var missing = CreateArchive("{}", "Other/file.json");
        using var duplicate = CreateArchive("{}", "Project\\sequence.json", "Project/sequence.json");

        Assert.Throws<InvalidDataException>(() => VprUtility.Deserialize(missing));
        Assert.Throws<InvalidDataException>(() => VprUtility.Deserialize(duplicate));
    }

    [Theory]
    [InlineData("{")]
    public void Deserialize_RejectsMalformedArchivesAndJson(string content)
    {
        using var stream = CreateArchive(content);

        Assert.ThrowsAny<Exception>(() => VprUtility.Deserialize(stream));
    }

    [Fact]
    public void Deserialize_SkipsNonObjectTrackEntries()
    {
        using var stream = CreateArchive("{\"tracks\":[null]}");

        var project = VprUtility.Deserialize(stream);

        Assert.Empty(project.Tracks);
    }

    static MemoryStream CreateArchive(string sequence, params string[] entryNames)
    {
        if (entryNames.Length == 0)
            entryNames = ["Project/sequence.json"];

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entryNames)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(sequence);
            }
        }
        stream.Position = 0;
        return stream;
    }

    const string SampleV6 = """
        {
          "version": { "major": 6 },
          "masterTrack": {
            "customMaster": "master-extra",
            "tempo": { "events": [{ "pos": 0, "value": 12000 }, { "pos": 960, "value": 8850 }] },
            "timeSig": { "events": [{ "bar": 2, "numer": 3, "denom": 4 }] }
          },
          "tracks": [{
            "name": "Lead",
            "volume": { "events": [{ "value": -120 }] },
            "panpot": { "events": [{ "value": -32 }] },
            "isMuted": true,
            "customTrack": "track-extra",
            "parts": [{
              "name": "Verse", "pos": 240, "duration": 1920, "customPart": "part-extra",
              "voice": { "compID": "voice-id" },
              "midiEffects": [{ "id": "VoiceColor", "parameters": {} }],
              "notes": [{ "pos": 120, "duration": 360, "number": 67, "lyric": "hello", "phoneme": "h e l o", "customNote": "note-extra" }],
              "controllers": [
                { "name": "dynamics", "events": [{ "pos": 0, "value": 0 }, { "pos": 120, "value": 127 }] },
                { "name": "pitchBend", "events": [{ "pos": 0, "value": -8192 }, { "pos": 120, "value": 8191 }] }
              ]
            }]
          }]
        }
        """;

    const string SampleV5 = """
        {
          "version": { "major": 5 },
          "tracks": [{ "parts": [{ "voice": { "compID": "old-voice" }, "notes": [] }] }]
        }
        """;
}
