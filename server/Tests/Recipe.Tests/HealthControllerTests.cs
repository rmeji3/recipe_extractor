using System.Net;

namespace Recipe.Tests;

public class HealthControllerTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/v1/health")]
    public async Task Get_returns_ok_on_both_route_shapes(string url)
    {
        var client = fixture.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
