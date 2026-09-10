using System.ComponentModel.DataAnnotations;
using LyWaf.Control;
using Xunit;

namespace LyWaf.Tests;

public sealed class UpstreamHealthSettingsTests
{
    [Theory]
    [InlineData("200", true)]
    [InlineData("200,204", true)]
    [InlineData("2xx,3xx", true)]
    [InlineData("600", false)]
    [InlineData("", false)]
    [InlineData("200,garbage", false)]
    public void ValidatesStatusCodes(string codes, bool expected)
    {
        var value = new ControlApi.UpstreamHealthSettings(StatusCodes: codes);
        Assert.Equal(expected, Validator.TryValidateObject(value, new ValidationContext(value), [], true));
    }

    [Fact]
    public void RejectsInvalidProbeSettings()
    {
        foreach (var value in new[]
        {
            new ControlApi.UpstreamHealthSettings(Path: "https://other/"),
            new ControlApi.UpstreamHealthSettings(Method: "INVALID"),
            new ControlApi.UpstreamHealthSettings(IntervalSeconds: 0),
            new ControlApi.UpstreamHealthSettings(TimeoutSeconds: 0),
            new ControlApi.UpstreamHealthSettings(Passes: 0),
            new ControlApi.UpstreamHealthSettings(Body: new string('x', 65537))
        }) Assert.False(Validator.TryValidateObject(value, new ValidationContext(value), [], true));
    }
}
