using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Npgsql;
using Newtonsoft.Json;
using Amazon.SQS;
using Amazon.SQS.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TareasApi;

public class Tarea
{
    public Guid Id { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descripcion { get; set; }
    public bool Completada { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Function
{
    private readonly string _connString;
    private readonly string _sqsUrl;
    private readonly AmazonSQSClient _sqs;
    public Function()
    {
        _connString = Environment.GetEnvironmentVariable("DB_CONNECTION") ?? "";
        _sqsUrl = Environment.GetEnvironmentVariable("SQS_URL") ?? "";
        _sqs = new AmazonSQSClient();
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request,
        ILambdaContext context)
    {
        var method = request.RequestContext.Http.Method;
        var path = request.RequestContext.Http.Path;
        var pathParams = request.PathParameters;
        var startTime = DateTime.UtcNow;

        try
        {
            var response = (method, pathParams) switch
            {
                ("GET", null) => await GetTareas(),
                ("GET", _) => await GetTarea(pathParams["id"]),
                ("POST", _) => await CrearTarea(request.Body),
                ("PUT", _) => await ActualizarTarea(pathParams["id"], request.Body),
                ("DELETE", _) => await EliminarTarea(pathParams["id"]),
                _ => Respuesta(404, new { error = "Ruta no encontrada" })
            };

            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.Log(context, "INFO", "Request completado", new
            {
                method,
                path,
                statusCode = response.StatusCode,
                durationMs = Math.Round(duration)
            });

            return response;
        }
        catch (Exception ex)
        {
            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            Logger.Log(context, "ERROR", ex.Message, new
            {
                method,
                path,
                durationMs = Math.Round(duration),
                exceptionType = ex.GetType().Name
            });
            return Respuesta(500, new { error = ex.Message });
        }
    }
    private async Task<APIGatewayHttpApiV2ProxyResponse> GetTareas()
    {
        var tareas = new List<Tarea>();
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT id, titulo, descripcion, completada, created_at FROM tareas ORDER BY created_at DESC", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tareas.Add(new Tarea
            {
                Id = reader.GetGuid(0),
                Titulo = reader.GetString(1),
                Descripcion = reader.IsDBNull(2) ? null : reader.GetString(2),
                Completada = reader.GetBoolean(3),
                CreatedAt = reader.GetDateTime(4)
            });
        }

        return Respuesta(200, tareas);
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> GetTarea(string id)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT id, titulo, descripcion, completada, created_at FROM tareas WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", Guid.Parse(id));
        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return Respuesta(404, new { error = "Tarea no encontrada" });

        return Respuesta(200, new Tarea
        {
            Id = reader.GetGuid(0),
            Titulo = reader.GetString(1),
            Descripcion = reader.IsDBNull(2) ? null : reader.GetString(2),
            Completada = reader.GetBoolean(3),
            CreatedAt = reader.GetDateTime(4)
        });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> CrearTarea(string body)
    {
        var input = JsonConvert.DeserializeObject<Tarea>(body)
            ?? throw new Exception("Body inválido");

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(@"
        INSERT INTO tareas (titulo, descripcion)
        VALUES (@titulo, @descripcion)
        RETURNING id, titulo, descripcion, completada, created_at", conn);

        cmd.Parameters.AddWithValue("titulo", input.Titulo);
        cmd.Parameters.AddWithValue("descripcion", (object?)input.Descripcion ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        var tarea = new Tarea
        {
            Id = reader.GetGuid(0),
            Titulo = reader.GetString(1),
            Descripcion = reader.IsDBNull(2) ? null : reader.GetString(2),
            Completada = reader.GetBoolean(3),
            CreatedAt = reader.GetDateTime(4)
        };

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _sqsUrl,
            MessageBody = JsonConvert.SerializeObject(new
            {
                tareaId = tarea.Id,
                titulo = tarea.Titulo,
                descripcion = tarea.Descripcion,
                evento = "tarea_creada"
            })
        });

        return Respuesta(201, tarea);
    }
    private async Task<APIGatewayHttpApiV2ProxyResponse> ActualizarTarea(string id, string body)
    {
        var input = JsonConvert.DeserializeObject<Tarea>(body)
            ?? throw new Exception("Body inválido");

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(@"
            UPDATE tareas
            SET titulo = @titulo, descripcion = @descripcion, completada = @completada
            WHERE id = @id
            RETURNING id, titulo, descripcion, completada, created_at", conn);

        cmd.Parameters.AddWithValue("id", Guid.Parse(id));
        cmd.Parameters.AddWithValue("titulo", input.Titulo);
        cmd.Parameters.AddWithValue("descripcion", (object?)input.Descripcion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completada", input.Completada);

        await using var reader = await cmd.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
            return Respuesta(404, new { error = "Tarea no encontrada" });

        return Respuesta(200, new Tarea
        {
            Id = reader.GetGuid(0),
            Titulo = reader.GetString(1),
            Descripcion = reader.IsDBNull(2) ? null : reader.GetString(2),
            Completada = reader.GetBoolean(3),
            CreatedAt = reader.GetDateTime(4)
        });
    }

    private async Task<APIGatewayHttpApiV2ProxyResponse> EliminarTarea(string id)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "DELETE FROM tareas WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", Guid.Parse(id));

        var rows = await cmd.ExecuteNonQueryAsync();

        return rows == 0
            ? Respuesta(404, new { error = "Tarea no encontrada" })
            : Respuesta(200, new { message = "Tarea eliminada correctamente" });
    }

    private static APIGatewayHttpApiV2ProxyResponse Respuesta(int statusCode, object? body)
    {
        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "Access-Control-Allow-Origin", "*" }
            },
            Body = body == null ? "" : JsonConvert.SerializeObject(body)
        };
    }
}

public static class Logger
{
    public static void Log(ILambdaContext context, string level, string message, object? extra = null)
    {
        var log = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["level"] = level,
            ["service"] = "TareasApi",
            ["requestId"] = context.AwsRequestId,
            ["message"] = message
        };

        if (extra != null)
            foreach (var prop in extra.GetType().GetProperties())
                log[prop.Name] = prop.GetValue(extra);

        context.Logger.LogInformation(JsonConvert.SerializeObject(log));
    }
}