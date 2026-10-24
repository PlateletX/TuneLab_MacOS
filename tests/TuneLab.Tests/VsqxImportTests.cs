using System.Text;
using System.IO;
using System.Xml.Linq;
using TuneLab.Extensions.Formats.Vsqx;
using TuneLab.SDK;
using Xunit;

namespace TuneLab.Tests;

public sealed class VsqxImportTests
{
    [Fact]
    public void Deserialize_PreservesNotesLyricsTempoSignaturesControllersAndAudioReferences()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Sample));

        var project = VsqxUtility.Deserialize(stream);

        Assert.Equal(2, project.Tempos.Count);
        Assert.Equal([0, 960], project.Tempos.Select(tempo => tempo.Pos));
        Assert.Equal([100.0, 88.5], project.Tempos.Select(tempo => tempo.Bpm));
        Assert.Equal(2, project.TimeSignatures.Count);
        Assert.Equal([0, 3], project.TimeSignatures.Select(signature => signature.BarIndex));
        Assert.Equal([4, 3], project.TimeSignatures.Select(signature => signature.Numerator));
        Assert.Equal([4, 4], project.TimeSignatures.Select(signature => signature.Denominator));

        Assert.Equal(2, project.Tracks.Count);
        var voiceTrack = project.Tracks[0];
        Assert.Equal("Lead", voiceTrack.Name);
        var part = Assert.IsType<MidiPartInfo>(Assert.Single(voiceTrack.Parts));
        Assert.Equal("Verse", part.Name);
        Assert.Equal(480, part.Pos);
        Assert.Equal(1920, part.EndOffset);
        Assert.Equal("YuezhenglingV3", part.Properties.GetString("vsqx.voiceName"));
        Assert.Contains("<bs>4</bs>", part.Properties.GetString("vsqx.singerXml"));

        Assert.Equal(2, part.Notes.Count);
        Assert.Collection(part.Notes,
            first =>
            {
                Assert.Equal(120, first.Pos);
                Assert.Equal(240, first.Dur);
                Assert.Equal(65, first.Pitch);
                Assert.Equal("chan", first.Lyric);
                Assert.Equal("ts`_h a_n", first.Pronunciation);
                Assert.Equal(64d, first.Properties.GetDouble("vsqx.velocity"));
                Assert.Contains("vibLen", first.Properties.GetString("vsqx.nStyleXml"));
            },
            second =>
            {
                Assert.Equal(360, second.Pos);
                Assert.Equal(480, second.Dur);
                Assert.Equal(68, second.Pitch);
                Assert.Equal("shi", second.Lyric);
            });

        Assert.Equal(4, part.Automations.Count);
        Assert.Equal(64d, part.Automations["Dynamics"].DefaultValue);
        Assert.Equal(new TuneLab.Foundation.Point(0, 64), part.Automations["Dynamics"].Points[0]);
        Assert.Equal(new TuneLab.Foundation.Point(480, 52), part.Automations["Dynamics"].Points[1]);
        Assert.Equal(0d, part.Automations["Volume"].DefaultValue);
        Assert.Equal(-2.25d, part.Automations["Volume"].Points[1].Y);
        Assert.Equal(0d, part.Automations["PitchBend"].DefaultValue);
        Assert.Equal(12d, part.Automations["PitchBendSensitivity"].DefaultValue);

        var audioPart = Assert.IsType<AudioPartInfo>(Assert.Single(project.Tracks[1].Parts));
        Assert.Equal("I:\\Music\\guide.wav", audioPart.Path);
        Assert.Equal(1920, audioPart.Pos);
        Assert.Equal(960, audioPart.EndOffset);
    }

    [Fact]
    public void Deserialize_RejectsDocumentsWithDtdOrUnexpectedRoot()
    {
        const string dtd = "<!DOCTYPE vsq4 [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><vsq4>&x;</vsq4>";
        using var dtdStream = new MemoryStream(Encoding.UTF8.GetBytes(dtd));
        Assert.Throws<System.Xml.XmlException>(() => VsqxUtility.Deserialize(dtdStream));

        using var wrongRoot = new MemoryStream(Encoding.UTF8.GetBytes("<notVsqx />"));
        Assert.Throws<InvalidDataException>(() => VsqxUtility.Deserialize(wrongRoot));
    }

    [Fact]
    public void Deserialize_ResolvesWindowsAudioReferencesBesideSourceFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TuneLab-Vsqx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string audioPath = Path.Combine(directory, "guide.wav");
        File.WriteAllBytes(audioPath, []);

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Sample));
            var project = VsqxUtility.Deserialize(stream, Path.Combine(directory, "project.vsqx"));

            var audioPart = Assert.IsType<AudioPartInfo>(Assert.Single(project.Tracks[1].Parts));
            Assert.Equal(audioPath, audioPart.Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Serialize_RoundTripsEditableContentAndRetainsVocaloidMetadata()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Sample));
        var original = VsqxUtility.Deserialize(input);
        using var output = new MemoryStream();

        VsqxUtility.Serialize(output, original);
        output.Position = 0;
        var roundTripped = VsqxUtility.Deserialize(output);

        Assert.Equal(original.Tempos.Select(tempo => (tempo.Pos, tempo.Bpm)),
            roundTripped.Tempos.Select(tempo => (tempo.Pos, tempo.Bpm)));
        Assert.Equal(original.TimeSignatures.Select(signature => (signature.BarIndex, signature.Numerator, signature.Denominator)),
            roundTripped.TimeSignatures.Select(signature => (signature.BarIndex, signature.Numerator, signature.Denominator)));

        var originalPart = Assert.IsType<MidiPartInfo>(Assert.Single(original.Tracks[0].Parts));
        var roundTrippedPart = Assert.IsType<MidiPartInfo>(Assert.Single(roundTripped.Tracks[0].Parts));
        Assert.Equal(originalPart.Pos, roundTrippedPart.Pos);
        Assert.Equal(originalPart.EndOffset, roundTrippedPart.EndOffset);
        Assert.Equal(originalPart.Notes.Select(note => (note.Pos, note.Dur, note.Pitch, note.Lyric, note.Pronunciation)),
            roundTrippedPart.Notes.Select(note => (note.Pos, note.Dur, note.Pitch, note.Lyric, note.Pronunciation)));
        Assert.Equal(originalPart.Automations["Dynamics"].Points, roundTrippedPart.Automations["Dynamics"].Points);
        Assert.Equal(originalPart.Automations["PitchBend"].Points, roundTrippedPart.Automations["PitchBend"].Points);
        Assert.Contains("VOCALOID2 Compatible Style", roundTrippedPart.Properties.GetString("vsqx.sPlugXml"));
        Assert.Contains("accent", roundTrippedPart.Properties.GetString("vsqx.pStyleXml"));
        Assert.Contains("vibLen", roundTrippedPart.Notes[0].Properties.GetString("vsqx.nStyleXml"));
        Assert.Contains("I:\\Music\\guide.wav", roundTripped.Tracks[1].Parts.OfType<AudioPartInfo>().Single().Path);

        var xml = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        var masterTrack = xml.Root!.Element(XName.Get("masterTrack", "http://www.yamaha.co.jp/vocaloid/schema/vsq4/"))!;
        Assert.True(masterTrack.Elements().Select(element => element.Name.LocalName).ToList().IndexOf("resolution")
            < masterTrack.Elements().Select(element => element.Name.LocalName).ToList().IndexOf("timeSig"));
    }

    [Fact]
    public void Serialize_PreservesStereoAudioTrackKind()
    {
        var project = new ProjectInfo();
        project.Tracks.Add(new TrackInfo
        {
            Name = "VOCALOID Stereo Audio",
            Parts =
            {
                new AudioPartInfo { Name = "Backing", Path = "/tmp/backing.wav", Pos = 240, EndOffset = 960 },
            },
        });
        using var output = new MemoryStream();

        VsqxUtility.Serialize(output, project);
        output.Position = 0;
        var roundTripped = VsqxUtility.Deserialize(output);

        var audioTrack = Assert.Single(roundTripped.Tracks);
        Assert.Equal("VOCALOID Stereo Audio", audioTrack.Name);
        var part = Assert.IsType<AudioPartInfo>(Assert.Single(audioTrack.Parts));
        Assert.Equal("/tmp/backing.wav", part.Path);
        Assert.Equal(240, part.Pos);
        Assert.Equal(960, part.EndOffset);
    }

    const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <vsq4 xmlns="http://www.yamaha.co.jp/vocaloid/schema/vsq4/">
          <vVoiceTable><vVoice><bs>4</bs><pc>0</pc><name>YuezhenglingV3</name></vVoice></vVoiceTable>
          <mixer />
          <masterTrack>
            <resolution>960</resolution>
            <timeSig><m>0</m><nu>4</nu><de>4</de></timeSig>
            <timeSig><m>3</m><nu>3</nu><de>4</de></timeSig>
            <tempo><t>0</t><v>10000</v></tempo>
            <tempo><t>1920</t><v>8850</v></tempo>
          </masterTrack>
          <vsTrack>
            <tNo>0</tNo><name>Lead</name><comment>main</comment>
            <vsPart>
              <t>960</t><playTime>3840</playTime><name>Verse</name><comment>verse</comment>
              <sPlug><id>style-id</id><name>VOCALOID2 Compatible Style</name><version>3.0.0.1</version></sPlug>
              <pStyle><v id="accent">50</v></pStyle>
              <singer><t>0</t><bs>4</bs><pc>0</pc></singer>
              <cc><t>0</t><v id="D">64</v></cc>
              <cc><t>0</t><v id="P">0</v></cc>
              <cc><t>0</t><v id="S">12</v></cc>
              <cc><t>960</t><v id="D">52</v></cc>
              <note>
                <t>240</t><dur>480</dur><n>65</n><v>64</v><y>chan</y><p>ts`_h a_n</p>
                <nStyle><v id="vibLen">66</v><seq id="vibRate"><cc><p>22391</p><v>50</v></cc></seq></nStyle>
              </note>
              <note><t>720</t><dur>960</dur><n>68</n><v>80</v><y>shi</y><p>shi</p></note>
            </vsPart>
          </vsTrack>
          <monoTrack>
            <wavPart><t>3840</t><playTime>1920</playTime><name>Guide</name><filePath>I:\Music\guide.wav</filePath></wavPart>
          </monoTrack>
        </vsq4>
        """;
}
