using GitSpread.Core.Security;

namespace GitSpread.Tests.Security;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("token gho_16C7e42F292c6912E7710c838347Ae178B4a fin", "token *** fin")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", "***")]
    [InlineData("pat github_pat_11ABCDEFG0123456789_abcdefghijklmnop", "pat ***")]
    [InlineData("Authorization: Basic U2FtOmdob19zZWNyZXQ=", "Authorization: Basic ***")]
    [InlineData("authorization: bearer abc.def", "authorization: bearer ***")]
    public void Masks_tokens_and_auth_headers(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input));

    [Theory]
    [InlineData("git push origin main")]
    [InlineData("branche gh_pages et ghost_user")]
    [InlineData("")]
    public void Leaves_ordinary_text_untouched(string input) =>
        Assert.Equal(input, SecretRedactor.Redact(input));
}
