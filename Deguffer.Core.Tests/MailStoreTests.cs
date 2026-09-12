using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The one question every removal, measurement and plan asks of a file before it goes. It is small,
/// and it is asked of everything Deguffer touches, so both directions are pinned: a mail store in any
/// spelling is one, and a name that only resembles one is not.
/// </summary>
public sealed class MailStoreTests
{
    [Theory]
    [InlineData("archive.pst")]
    [InlineData("someone@example.com.ost")]
    [InlineData("ARCHIVE.PST")]
    [InlineData("Mailbox.Ost")]
    [InlineData(@"C:\Users\testuser\Documents\Outlook Files\archive.pst")]
    [InlineData(@"\\?\C:\Users\testuser\AppData\Local\Microsoft\Outlook\someone@example.com.ost")]
    [InlineData(@"D:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001\$RA1B2C3.pst")]
    public void AnOstOrPstInAnySpellingIsOne(string nameOrPath) =>
        Assert.True(MailStore.Is(nameOrPath));

    [Theory]
    [InlineData("archive.pst.txt")]
    [InlineData("archive.pstx")]
    [InlineData("archive.post")]
    [InlineData("pst")]
    [InlineData("Outlook Files")]
    [InlineData(@"C:\Users\testuser\mail.pst\readme.txt")]
    [InlineData("")]
    public void ANameThatOnlyResemblesOneIsNot(string nameOrPath) =>
        Assert.False(MailStore.Is(nameOrPath));
}
