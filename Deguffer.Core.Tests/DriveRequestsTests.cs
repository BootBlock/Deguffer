using System.Buffers.Binary;
using Deguffer.Core.Health;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: Health sends a drive nothing that could change it. Every request is decoded here, so a
/// command added to the table fails this test until someone has said what it is.
/// </summary>
public sealed class DriveRequestsTests
{
    private const uint QueryProperty = 0x002D_1400;
    private const uint SmartRcvDriveData = 0x0007_C088;
    private const int NvmeHealthPage = 2;
    private const int NvmeSelfTestPage = 6;

    public static TheoryData<DriveQuestion> EveryQuestion
    {
        get
        {
            var data = new TheoryData<DriveQuestion>();

            foreach (var question in Enum.GetValues<DriveQuestion>())
            {
                data.Add(question);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryQuestion))]
    public void EveryQuestionIsARecognisedRead(DriveQuestion question)
    {
        var request = DriveRequests.For(question);

        if (request.ControlCode == SmartRcvDriveData)
        {
            AssertReadOnlyAtaCommand(question, request);
            return;
        }

        Assert.Equal(DriveAccess.Query, request.Access);
        Assert.Contains(request.ControlCode, new uint[] { QueryProperty, 0x002D_1080, 0x002D_1100, 0x0007_00A0 });

        if (request.ControlCode == QueryProperty)
        {
            AssertReadOnlyProperty(question, request.Input);
        }
    }

    [Theory]
    [InlineData(DriveQuestion.NvmeHealth, NvmeHealthPage, 2)]
    [InlineData(DriveQuestion.NvmeSelfTest, NvmeSelfTestPage, 2)]
    [InlineData(DriveQuestion.NvmeController, 1, 1)]
    public void NvmeQuestionsAskForTheirLogOrIdentifyPage(DriveQuestion question, int requestValue, int dataType)
    {
        var input = DriveRequests.For(question).Input;

        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(input));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(8)));
        Assert.Equal(dataType, BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(12)));
        Assert.Equal(requestValue, BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(16)));
    }

    private static void AssertReadOnlyProperty(DriveQuestion question, byte[] input)
    {
        Assert.Contains(BinaryPrimitives.ReadInt32LittleEndian(input), new[] { 0, 6, 7, 8, 50 });

        if (BinaryPrimitives.ReadInt32LittleEndian(input) == 50)
        {
            Assert.Contains(question, new[] { DriveQuestion.NvmeHealth, DriveQuestion.NvmeSelfTest, DriveQuestion.NvmeController });
        }
    }

    private static void AssertReadOnlyAtaCommand(DriveQuestion question, DriveRequest request)
    {
        Assert.Equal(DriveAccess.Command, request.Access);

        var registers = request.Input.AsSpan(DriveRequests.AtaRegistersOffset);
        var feature = registers[0];
        var sectorNumber = registers[2];
        var command = registers[6];

        switch (question)
        {
            case DriveQuestion.AtaIdentify:
                Assert.Equal(0xEC, command);
                Assert.Equal(0, feature);
                break;

            case DriveQuestion.AtaThresholds:
                Assert.Equal(0xB0, command);
                Assert.Equal(0xD1, feature);
                break;

            case DriveQuestion.AtaErrorLog:
                Assert.Equal(0xB0, command);
                Assert.Equal(0xD5, feature);
                Assert.Equal(0x01, sectorNumber);
                break;

            case DriveQuestion.AtaSelfTestLog:
                Assert.Equal(0xB0, command);
                Assert.Equal(0xD5, feature);
                Assert.Equal(0x06, sectorNumber);
                break;

            default:
                Assert.Fail($"{question} sends an ATA command nothing here has reviewed.");
                break;
        }
    }
}
