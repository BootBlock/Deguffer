using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a location typed into Explore names. Nothing confirmed a typed path as the shell confirms a
/// picked one, so anything that is not a full path to a folder is refused, and what the page says
/// tells a name typed wrong apart from a share that would not answer.
/// </summary>
public sealed class TypedFolderTests
{
    [Theory]
    [InlineData(@"\\server.test\share", @"\\server.test\share")]
    [InlineData(@"\\server.test\share\", @"\\server.test\share\")]
    [InlineData(@"  \\server.test\share\Photos  ", @"\\server.test\share\Photos")]
    [InlineData(@"""\\server.test\share\Photos""", @"\\server.test\share\Photos")]
    [InlineData(@"D:\Folder", @"D:\Folder")]
    [InlineData(@"\\?\UNC\server.test\share\Photos", @"\\server.test\share\Photos")]
    [InlineData(@"\\?\D:\Folder", @"D:\Folder")]
    public void AFullPathIsTheFolderItNames(string typed, string folder) =>
        Assert.Equal(folder, TypedFolder.Folder(typed));

    /// <summary>
    /// A trailing dot or space is part of a folder's name, and resolving the path would scan the
    /// sibling without it. Inside quotes, a trailing space is part of the name too.
    /// </summary>
    [Theory]
    [InlineData(@"\\server.test\share\build.", @"\\server.test\share\build.")]
    [InlineData(@"""\\server.test\share\build """, @"\\server.test\share\build ")]
    public void AFolderNamedWithATrailingDotOrSpaceKeepsIt(string typed, string folder) =>
        Assert.Equal(folder, TypedFolder.Folder(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"""""")]
    [InlineData("share")]
    [InlineData(@"server.test\share")]
    [InlineData(@"D:Folder")]
    [InlineData(@"\Folder")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\Volume{6f3c1a2e-0000-0000-0000-100000000000}\")]
    public void WhatIsNotAFullPathToAFolderIsNothing(string typed) =>
        Assert.Null(TypedFolder.Folder(typed));

    [Fact]
    public void AFolderThatIsThereIsNotRefused() =>
        Assert.Null(TypedFolder.Refusal(@"\\server.test\share", PathPresence.Present));

    /// <summary>
    /// A share that would not say is not a share that is not there: the one is fixed at the server or
    /// in its permissions, the other by typing the name again.
    /// </summary>
    [Fact]
    public void AnAbsentFolderAndOneWindowsWouldNotDescribeAreToldApart()
    {
        var absent = TypedFolder.Refusal(@"\\server.test\share", PathPresence.Absent);
        var refused = TypedFolder.Refusal(@"\\server.test\share", PathPresence.Refused);

        Assert.Contains(@"There is no folder at '\\server.test\share'", absent);
        Assert.Contains(@"Windows would not say whether '\\server.test\share' is a folder", refused);
        Assert.NotEqual(absent, refused);
    }
}
