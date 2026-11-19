using Deguffer.Core.VirtualDisks;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The <c>ParentPath</c> of the CIM-XML instance <c>GetVirtualHardDiskSettingData</c> gives: a parent, a
/// disk built on nothing, and every answer that says neither, which must never be read as nothing.
/// </summary>
public sealed class HyperVDiskSettingDataTests
{
    private const string Disk = @"C:\Users\testuser\Hyper-V\Dev_7F3A.avhdx";

    private const string Base = @"C:\Users\testuser\Hyper-V\R&D <base>.vhdx";

    [Fact]
    public void AParentIsReadFromItsOwnPropertyAndNotFromOneWithASimilarName()
    {
        Assert.True(HyperVDiskSettingData.TryReadParent(FakeHyperV.SettingData(Disk, Base), out var parent));
        Assert.Equal(Base, parent);
    }

    /// <summary>
    /// Microsoft: "If the virtual hard disk does not have a parent, then this property is empty". Both
    /// forms of empty are a disk built on nothing: no <c>VALUE</c>, and an empty one.
    /// </summary>
    [Theory]
    [InlineData("<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"></PROPERTY>")]
    [InlineData("<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"/>")]
    [InlineData("<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"><VALUE></VALUE></PROPERTY>")]
    [InlineData("<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"><VALUE/></PROPERTY>")]
    public void AnEmptyParentIsADiskBuiltOnNothing(string property)
    {
        var settingData = $"<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\">{property}<PROPERTY NAME=\"Path\" TYPE=\"string\"><VALUE>{Disk}</VALUE></PROPERTY></INSTANCE>";

        Assert.True(HyperVDiskSettingData.TryReadParent(settingData, out var parent));
        Assert.Null(parent);
        Assert.True(HyperVDiskSettingData.TryReadParent(FakeHyperV.SettingData(Disk, null), out parent));
        Assert.Null(parent);
    }

    /// <summary>
    /// An answer that does not say what the disk is built on is no answer: taking it for a disk built on
    /// nothing would end the chain early and leave the disks under it unregistered. A document type is
    /// refused, so nothing in the answer is expanded or fetched. Mutation: returning true with no parent
    /// when the property is missing passes the first case.
    /// </summary>
    [Theory]
    [InlineData("<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\"><PROPERTY NAME=\"Path\" TYPE=\"string\"><VALUE>x</VALUE></PROPERTY></INSTANCE>")]
    [InlineData("<INSTANCE CLASSNAME=\"Msvm_ResourceAllocationSettingData\"><PROPERTY NAME=\"ParentPath\" TYPE=\"string\"><VALUE>C:\\a.vhdx</VALUE></PROPERTY></INSTANCE>")]
    [InlineData("<VALUE>C:\\a.vhdx</VALUE>")]
    [InlineData("<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\"><PROPERTY NAME=\"ParentPath\"")]
    [InlineData("<!DOCTYPE INSTANCE [<!ENTITY p \"C:\\a.vhdx\">]><INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\"><PROPERTY NAME=\"ParentPath\" TYPE=\"string\"><VALUE>&p;</VALUE></PROPERTY></INSTANCE>")]
    [InlineData("")]
    [InlineData(null)]
    public void AnAnswerThatDoesNotSayIsNotTakenForADiskBuiltOnNothing(string? settingData)
    {
        Assert.False(HyperVDiskSettingData.TryReadParent(settingData, out var parent));
        Assert.Null(parent);
    }

    [Fact]
    public void AnAnswerFarLargerThanAnyDescriptionIsNotRead()
    {
        var padding = new string(' ', 70 * 1024);
        var settingData = $"<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\">{padding}<PROPERTY NAME=\"ParentPath\" TYPE=\"string\"><VALUE>{Disk}</VALUE></PROPERTY></INSTANCE>";

        Assert.False(HyperVDiskSettingData.TryReadParent(settingData, out _));
    }
}
