using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;
using Task = System.Threading.Tasks.Task;

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
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

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
            var logger = serviceProvider.GetRequiredService<ILogger<FhirOutputFormatter>>();
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
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

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

            // A well-formed resource of another type than the action takes - a Bundle posted to
            // an operation expecting Parameters - is a failed read, the same as malformed JSON.
            // Handed on as a success, MVC would cast it to the parameter's type and fail with a
            // 500 instead of the action answering it with an OperationOutcome.
            if (resource is not null && !context.ModelType.IsInstanceOfType(resource))
            {
                var serviceProvider = httpContext.RequestServices;
                var logger = serviceProvider.GetRequiredService<ILogger<FhirInputFormatter>>();
                logger.LogWarning(
                    "Received a FHIR {ResourceType} resource where a {ExpectedType} was expected",
                    resource.TypeName,
                    context.ModelType.Name
                );
                return await InputFormatterResult.FailureAsync();
            }

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
