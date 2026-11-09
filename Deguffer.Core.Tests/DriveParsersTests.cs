using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Each structure a drive answers with, read from synthesised bytes. A short or malformed answer
/// reads as no answer, because a zero read past the end would pass for a drive's reading.
/// </summary>
public sealed class DriveParsersTests
{
    [Fact]
    public void ADeviceDescriptorGivesItsStringsWithoutPadding()
    {
        var bytes = DriveBytes.Device(DriveBytes.Sata, "  Test Model  ", serial: " TESTSERIAL0001 ", vendor: "Acme", revision: "2.1");

        Assert.True(DeviceDescriptor.TryRead(bytes, out var descriptor));
        Assert.Equal("Acme Test Model", descriptor.Model);
        Assert.Equal("2.1", descriptor.Revision);
        Assert.Equal("TESTSERIAL0001", descriptor.Serial.Text);
    }

    [Fact]
    public void AnAbsentOrOutOfRangeDescriptorStringReadsAsEmpty()
    {
        var bytes = DriveBytes.Device(DriveBytes.Sata, "Model", serial: null);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), bytes.Length + 8);

        Assert.True(DeviceDescriptor.TryRead(bytes, out var descriptor));
        Assert.Equal(string.Empty, descriptor.Serial.Text);
        Assert.Equal(string.Empty, descriptor.Revision);
    }

    [Fact]
    public void ADescriptorTooShortForItsFieldsIsNoAnswer() =>
        Assert.False(DeviceDescriptor.TryRead(new byte[31], out _));

    [Fact]
    public void AnNvmeHealthLogGivesItsFields()
    {
        var bytes = DriveBytes.NvmeHealth(criticalWarning: 4, kelvin: 320, spare: 90, spareThreshold: 5, percentageUsed: 12, powerOnHours: 1000, mediaErrors: 3, sensors: [320, 330]);

        Assert.True(NvmeHealthLog.TryRead(bytes, out var log));
        Assert.Equal((byte)4, log!.CriticalWarning);
        Assert.Equal(320, log.TemperatureKelvin);
        Assert.Equal((byte)90, log.AvailableSpare);
        Assert.Equal((byte)5, log.SpareThreshold);
        Assert.Equal((byte)12, log.PercentageUsed);
        Assert.Equal((UInt128)1000, log.PowerOnHours);
        Assert.Equal((UInt128)3, log.MediaErrors);
        Assert.Equal([320, 330], log.SensorKelvin);
    }

    [Fact]
    public void AProtocolAnswerIsReadFromTheOffsetItDeclares()
    {
        var bytes = new byte[8 + 64 + 512];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 64);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 512);
        bytes[8 + 64 + 5] = 42;

        Assert.True(NvmeHealthLog.TryRead(bytes, out var log));
        Assert.Equal((byte)42, log!.PercentageUsed);
    }

    [Theory]
    [InlineData(600, 512)]
    [InlineData(40, 10_000)]
    public void AProtocolAnswerDeclaringBeyondItsEndIsNoAnswer(int offset, int length)
    {
        var bytes = new byte[560];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), offset);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), length);

        Assert.False(NvmeHealthLog.TryRead(bytes, out _));
    }

    [Fact]
    public void AProtocolAnswerHoldingLessThanTheFieldsNeedIsNoAnswer() =>
        Assert.False(NvmeHealthLog.TryRead(new byte[100], out _));

    [Fact]
    public void AnNvmeControllerGivesItsVersionAndLimits()
    {
        Assert.True(NvmeController.TryRead(DriveBytes.NvmeController(1, 4, warningKelvin: 350), out var controller));
        Assert.Equal((1, 4), controller.Version);
        Assert.Equal(350, controller.WarningKelvin);
    }

    [Fact]
    public void AnAtaAnswerWithADriverErrorIsNoAnswer()
    {
        Assert.False(AtaCommandAnswer.TrySector(DriveBytes.Ata(new byte[512], driverError: 1), out _));
        Assert.True(AtaCommandAnswer.TrySector(DriveBytes.Ata(new byte[512]), out _));
    }

    [Fact]
    public void AShortAtaAnswerIsNoAnswer() =>
        Assert.False(AtaCommandAnswer.TrySector(new byte[100], out _));

    [Fact]
    public void AnAttributeEntryIsReadInFull()
    {
        var bytes = DriveBytes.Prediction(true, new AtaEntry(5, 90, Raw: 0x0102_0304_0506, PreFail: true, Worst: 80));

        Assert.True(AtaAttributeTable.TryReadPrediction(bytes, out var predicts, out var attributes));
        Assert.True(predicts);

        var attribute = Assert.Single(attributes);
        Assert.Equal((byte)5, attribute.Id);
        Assert.True(attribute.IsPreFail);
        Assert.Equal((byte)90, attribute.Value);
        Assert.Equal((byte)80, attribute.Worst);
        Assert.Equal(0x0102_0304_0506UL, attribute.Raw);
    }

    [Fact]
    public void AnAllZeroTableHasNoAttributes()
    {
        Assert.True(AtaAttributeTable.TryReadPrediction(new byte[516], out _, out var attributes));
        Assert.Empty(attributes);
    }

    [Fact]
    public void ARepeatedAttributeIdKeepsTheFirst()
    {
        var bytes = DriveBytes.Prediction(false, new AtaEntry(9, 70), new AtaEntry(9, 40));

        Assert.True(AtaAttributeTable.TryReadPrediction(bytes, out _, out var attributes));
        Assert.Equal((byte)70, Assert.Single(attributes).Value);
    }

    [Fact]
    public void ThresholdsAttachByAttribute()
    {
        Assert.True(AtaAttributeTable.TryReadThresholds(DriveBytes.Thresholds((5, 36)), out var thresholds));

        var attributes = AtaAttributeTable.WithThresholds([new AtaAttribute(5, true, 100, 100, 0, null), new AtaAttribute(9, false, 100, 100, 0, null)], thresholds);

        Assert.Equal((byte)36, attributes[0].Threshold);
        Assert.Null(attributes[1].Threshold);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0)]
    [InlineData(7200, 7200, 0, 0, 0)]
    public void IdentifyReadsRotationSpeed(int word, int expected, int capabilities, int current, int fastest)
    {
        Assert.True(AtaIdentify.TryRead(DriveBytes.Identify(word, capabilities, current), out var identify));
        Assert.Equal(expected, identify.RotationRpm);
        Assert.Equal(fastest, identify.FastestGeneration);
    }

    [Fact]
    public void IdentifyReadsLinkGenerations()
    {
        // Generations one to three, advertised in bits 1 to 3.
        Assert.True(AtaIdentify.TryRead(DriveBytes.Identify(1, 0b1110, 2), out var identify));
        Assert.Equal(3, identify.FastestGeneration);
        Assert.Equal(2, identify.CurrentGeneration);
    }

    [Fact]
    public void AnUnreportedRotationIsNull()
    {
        Assert.True(AtaIdentify.TryRead(DriveBytes.Identify(0), out var identify));
        Assert.Null(identify.RotationRpm);
    }

    [Fact]
    public void TheCircularAtaLogGivesTheEntryItsIndexNames()
    {
        var sector = DriveBytes.AtaSelfTest(index: 3, statusNibble: 7, hours: 500);

        Assert.True(SelfTestLogs.TryReadAta(sector, out var outcome));
        Assert.Equal(SelfTestResult.Failed, outcome.Result);
        Assert.Equal(500UL, outcome.PowerOnHours);
    }

    [Fact]
    public void AnEmptyAtaLogHasNoLatestEntry() =>
        Assert.False(SelfTestLogs.TryReadAta(new byte[512], out _));

    [Theory]
    [InlineData(0, (int)SelfTestResult.Passed)]
    [InlineData(1, (int)SelfTestResult.Stopped)]
    [InlineData(5, (int)SelfTestResult.Failed)]
    public void AnNvmeLogGivesItsNewestEntry(byte result, int expected)
    {
        Assert.True(SelfTestLogs.TryReadNvme(DriveBytes.NvmeSelfTest(result, 42), out var outcome));
        Assert.Equal((SelfTestResult)expected, outcome.Result);
        Assert.Equal(42UL, outcome.PowerOnHours);
    }

    [Fact]
    public void AnUnusedNvmeEntryIsNoResult() =>
        Assert.False(SelfTestLogs.TryReadNvme(DriveBytes.NvmeSelfTest(0xF), out _));

    [Fact]
    public void TheErrorLogGivesItsCount()
    {
        Assert.True(AtaErrorLog.TryReadCount(DriveBytes.AtaErrorLog(7), out var count));
        Assert.Equal(7, count);
    }

    [Fact]
    public void ASerialNeverPrintsThroughARecord()
    {
        var text = new DriveFact("Serial number", "TESTSERIAL0001").ToString() + new DriveSerial("TESTSERIAL0001");

        Assert.DoesNotContain("TESTSERIAL0001", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeySaysNothingOfTheSerial() =>
        Assert.DoesNotContain("TESTSERIAL0001", DriveKey.Of("Model", new DriveSerial("TESTSERIAL0001"), 0).Token, StringComparison.Ordinal);

    [Fact]
    public void InterfacePathsSplitAtEachTerminator() =>
        Assert.Equal(["first", "second"], DeviceInterfaces.Split("first\0second\0\0tail".AsSpan()));

    [Fact]
    public void AnEmptyMultiStringHasNoPaths() =>
        Assert.Empty(DeviceInterfaces.Split("\0".AsSpan()));
}
