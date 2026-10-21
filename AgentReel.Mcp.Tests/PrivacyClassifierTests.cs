using AgentReel.Editing.Privacy;
using Xunit;

namespace AgentReel.Mcp.Tests;

public class PrivacyClassifierTests
{
    private static TextMatch Single(string line, PrivacyKind kind)
    {
        var match = Assert.Single(PrivacyClassifier.Classify(line));
        Assert.Equal(kind, match.Kind);
        return match;
    }

    [Theory]
    [InlineData("OPENAI_KEY sk-proj-Ab3dEf6hIj9kLm2nOp5qRs8t", "sk-proj-Ab3dEf6hIj9kLm2nOp5qRs8t")]
    [InlineData("token ghp_R4nd0mT0k3nV4lu3sAb12CdEf34GhIj56Kl", "ghp_R4nd0mT0k3nV4lu3sAb12CdEf34GhIj56Kl")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijk", "github_pat_11ABCDEFG0123456789_abcdefghijk")]
    [InlineData("slack xoxb-1234567890-abcdefghij", "xoxb-1234567890-abcdefghij")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE in env", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("stripe sk_live_4eC39HqLyjWDarjtT1zdp7dc", "sk_live_4eC39HqLyjWDarjtT1zdp7dc")]
    [InlineData("claude sk-ant-api03-AbCdEf123456", "sk-ant-api03-AbCdEf123456")]
    public void Known_token_formats_are_api_keys(string line, string expected)
    {
        var match = Single(line, PrivacyKind.ApiKey);

        Assert.Equal(expected, match.Text);
        Assert.Equal(PrivacySeverity.High, match.Severity);
    }

    [Fact]
    public void A_jwt_is_found()
    {
        var match = Single("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", PrivacyKind.Jwt);

        Assert.StartsWith("eyJhbGci", match.Text);
        Assert.EndsWith("THsR8U", match.Text);
    }

    [Fact]
    public void A_private_key_header_is_found() => Single("-----BEGIN RSA PRIVATE KEY-----", PrivacyKind.PrivateKey);

    [Theory]
    [InlineData("Contact: jane.doe@example.com for access", "jane.doe@example.com")]
    [InlineData("From amir.test+news@mail.example.co.uk", "amir.test+news@mail.example.co.uk")]
    public void Emails_are_found(string line, string expected) => Assert.Equal(expected, Single(line, PrivacyKind.Email).Text);

    [Theory]
    [InlineData("IBAN: DE89370400440532013000")]
    [InlineData("IBAN DE89 3704 0044 0532 0130 00")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    public void Valid_ibans_are_found(string line) => Single(line, PrivacyKind.Iban);

    [Fact]
    public void An_iban_with_a_wrong_checksum_is_ignored() => Assert.DoesNotContain(PrivacyClassifier.Classify("DE00370400440532013000"), a => a.Kind == PrivacyKind.Iban);

    [Theory]
    [InlineData("Card 4111 1111 1111 1111 exp 12/29")]
    [InlineData("5500-0000-0000-0004")]
    public void Card_numbers_with_a_valid_checksum_are_found(string line) => Single(line, PrivacyKind.CreditCard);

    [Fact]
    public void A_long_number_with_a_wrong_checksum_is_not_a_card() => Assert.Empty(PrivacyClassifier.Classify("Order 4111 1111 1111 1112"));

    [Theory]
    [InlineData("Call +49 170 1234567 tomorrow")]
    [InlineData("Tel. +1 (415) 555-0132")]
    public void Phone_numbers_with_a_country_code_are_found(string line) => Single(line, PrivacyKind.Phone);

    [Theory]
    [InlineData("DB_PASSWORD=hunter22", "hunter22")]
    [InlineData("Passwort: Geheim!2026", "Geheim!2026")]
    [InlineData("client_secret = \"a8Fk2pQ9\"", "a8Fk2pQ9")]
    [InlineData("API_KEY: letmein-now", "letmein-now")]
    public void Values_after_secret_labels_are_passwords(string line, string expected) => Assert.Equal(expected, Single(line, PrivacyKind.Password).Text);

    [Theory]
    [InlineData("password: ********")]
    [InlineData("PASSWORD=${DB_PASSWORD}")]
    [InlineData("token: required")]
    [InlineData("Tests passed: 42/42")]
    [InlineData("Used tokens: 15234")]
    public void Placeholders_and_counts_are_not_passwords(string line) => Assert.DoesNotContain(PrivacyClassifier.Classify(line), a => a.Kind == PrivacyKind.Password);

    [Theory]
    [InlineData("secret Zx8Qw2Lp9Rt4Yu7Io1Pa3Sd6Fg0Hj5Kl")]
    [InlineData("export KEY Fh38dKq0Lz7NwX2pRt5vB9mYc4")]
    public void Random_tokens_are_secrets(string line) => Single(line, PrivacyKind.Secret);

    [Fact]
    public void An_ip_address_is_low_severity()
    {
        var match = Single("Server 192.168.178.20 is up", PrivacyKind.IpAddress);

        Assert.Equal(PrivacySeverity.Low, match.Severity);
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog, then reads the documentation again.")]
    [InlineData("Wir haben die Aufnahme gekürzt und den Export als GIF gespeichert.")]
    [InlineData(@"C:\Users\Anwender\dev\AgentReel\AgentReel.Studio\bin\Release\net9.0-windows10.0.19041.0")]
    [InlineData("src/AgentReel.Studio/bin/Release/net9.0-windows10.0.19041.0/AgentReel.Studio.dll")]
    [InlineData("commit 3f9e2c1a7b4d8e6f0a2c4e6b8d0f1a3c5e7b9d1f")]
    [InlineData("b9aa7216 Stop calling upstream services from the legacy app")]
    [InlineData("sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("guid 3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("AgentReelStudioSessionWindowRecordingBar extends FluentWindow")]
    [InlineData("Passed!  - Failed:     0, Passed:    44, Skipped:     0, Total:    44, Duration: 375 ms")]
    [InlineData("Version 2.43.2 released on 2026-10-21 at 15:07:34")]
    [InlineData("dotnet build GifRecorder.sln -c Release -v q -nologo")]
    public void Ordinary_text_is_not_flagged(string line) => Assert.Empty(PrivacyClassifier.Classify(line).Where(w => w.Severity > PrivacySeverity.Low));

    [Fact]
    public void Several_findings_in_one_line_are_found_in_order()
    {
        var matches = PrivacyClassifier.Classify("mail jane.doe@example.com key ghp_R4nd0mT0k3nV4lu3sAb12CdEf34GhIj56Kl");

        Assert.Equal([PrivacyKind.Email, PrivacyKind.ApiKey], matches.Select(s => s.Kind));
    }

    [Fact]
    public void Masks_never_reveal_the_value()
    {
        var mask = PrivacyClassifier.Mask(PrivacyKind.ApiKey, "ghp_R4nd0mT0k3nV4lu3sAb12CdEf34GhIj56Kl");

        Assert.Equal("ghp_… (39 chars)", mask);
        Assert.DoesNotContain("R4nd0m", mask);
        Assert.Equal("ja… (20 chars)", PrivacyClassifier.Mask(PrivacyKind.Email, "jane.doe@example.com"));
    }

    [Fact]
    public void Entropy_and_switch_ratio_tell_random_strings_from_names()
    {
        Assert.True(PrivacyClassifier.IsHighEntropySecret("Zx8Qw2Lp9Rt4Yu7Io1Pa3Sd6Fg0Hj5Kl"));
        Assert.False(PrivacyClassifier.IsHighEntropySecret("AgentReelStudioWindow2026"));
        Assert.False(PrivacyClassifier.IsHighEntropySecret("3f9e2c1a7b4d8e6f0a2c4e6b8d0f1a3c5e7b9d1f"));
    }
}
