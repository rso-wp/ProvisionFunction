using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Function.Features.Health;

public class HealthFunction
{
    /// <summary>GET /api/health: the worker process is up and the host routes to it. Anonymous, so uptime checks can call it.</summary>
    [Function("Health")]
    public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        var response = request.CreateResponse(System.Net.HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        response.WriteString("""{"status":"Healthy"}""");
        return response;
    }
}
