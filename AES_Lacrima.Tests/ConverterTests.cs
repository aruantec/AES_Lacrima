using System.Globalization;
using AES_Lacrima.Converters;

namespace AES_Lacrima.Tests;

public sealed class ConverterTests
{
    [Theory]
    [InlineData(65d, "01:05")]
    [InlineData(3661d, "01:01:01")]
    public void TimeSpanConverter_FormatsSeconds(double input, string expected)
    {
        var converter = new TimeSpanConverter();

        var result = converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BoolToStringConverter_UsesProvidedParameterValues()
    {
        var converter = new BoolToStringConverter();

        var trueResult = converter.Convert(true, typeof(string), "Enabled, Disabled", CultureInfo.InvariantCulture);
        var falseResult = converter.Convert(false, typeof(string), "Enabled, Disabled", CultureInfo.InvariantCulture);

        Assert.Equal("Enabled", trueResult);
        Assert.Equal("Disabled", falseResult);
    }

    [Theory]
    [InlineData("value", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StringNotEmptyToBoolConverter_ReturnsExpectedValue(string? input, bool expected)
    {
        var converter = new StringNotEmptyToBoolConverter();

        var result = converter.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ObjectEqualsConverter_ComparesValuesSafely()
    {
        var converter = new ObjectEqualsConverter();

        var equalResult = converter.Convert([42, 42], typeof(bool), null, CultureInfo.InvariantCulture);
        var notEqualResult = converter.Convert([42, 24], typeof(bool), null, CultureInfo.InvariantCulture);
        var nullResult = converter.Convert([null, null], typeof(bool), null, CultureInfo.InvariantCulture);

        Assert.Equal(true, equalResult);
        Assert.Equal(false, notEqualResult);
        Assert.Equal(true, nullResult);
    }
}
