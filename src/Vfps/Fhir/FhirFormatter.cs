using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc.Formatters;
using System.Text.Json;
using System.Text;
using Microsoft.Net.Http.Headers;
using Task = System.Threading.Tasks.Task;
using Hl7.Fhir.Serialization;

namespace Vfps.Fhir;

public class FhirOutputFormatter : TextOutputFormatter
{
    public FhirOutputFormatter()
    {
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/fhir+json"));
        SupportedEncodings.Add(Encoding.UTF8);
        SupportedEncodings.Add(Encoding.Unicode);
    }

    private JsonSerializerOptions FhirJsonOptions { get; } =
        new JsonSerializerOptions().ForFhir(typeof(Bundle).Assembly);

    protected override bool CanWriteType(Type? type)
    {
        return typeof(Resource).IsAssignableFrom(type);
    }

    public override async Task WriteResponseBodyAsync(
        OutputFormatterWriteContext context,
        Encoding selectedEncoding
    )
    {
        var resource = context.Object as Resource;
        var httpContext = context.HttpContext;

        try
        {
            var json = JsonSerializer.Serialize(resource, FhirJsonOptions);
            await httpContext.Response.WriteAsync(json);
        }
        catch (Exception exc)
        {
            var serviceProvider = httpContext.RequestServices;
            var logger = serviceProvider.GetRequiredService<ILogger<FhirInputFormatter>>();
            logger.LogError(exc, "Failed to serialize FHIR resource");

            throw;
        }
    }
}

public class FhirInputFormatter : TextInputFormatter
{
    public FhirInputFormatter()
    {
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/json"));
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/fhir+json"));
        SupportedEncodings.Add(Encoding.UTF8);
        SupportedEncodings.Add(Encoding.Unicode);
    }

    private JsonSerializerOptions FhirJsonOptions { get; } =
        new JsonSerializerOptions().ForFhir(typeof(Bundle).Assembly);

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(
        InputFormatterContext context,
        Encoding encoding
    )
    {
        var httpContext = context.HttpContext;
        using var reader = new StreamReader(httpContext.Request.Body, encoding);
        var json = await reader.ReadToEndAsync();
        try
        {
            var resource = JsonSerializer.Deserialize<Resource>(json, FhirJsonOptions);
            return await InputFormatterResult.SuccessAsync(resource);
        }
        catch (Exception exc)
        {
            var serviceProvider = httpContext.RequestServices;
            var logger = serviceProvider.GetRequiredService<ILogger<FhirInputFormatter>>();
            logger.LogError(exc, "Failed to parse the received FHIR resource");
            return await InputFormatterResult.FailureAsync();
        }
    }
}
