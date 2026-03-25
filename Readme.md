# TareasApi

API REST serverless para gestión de tareas, construida con AWS Lambda y .NET 8.

## Stack

- **Runtime:** AWS Lambda (.NET 8)
- **API:** Amazon API Gateway HTTP API (v2)
- **Base de datos:** Supabase PostgreSQL (via connection pooler)
- **Secrets:** AWS Systems Manager Parameter Store
- **Mensajería:** Amazon SQS — encola notificaciones al crear tareas
- **Observabilidad:** CloudWatch Logs (JSON estructurado) + CloudWatch Alarms
- **CI/CD:** GitHub Actions — deploy automático en cada push a `main`

## Endpoints

| Método | Ruta | Descripción |
|--------|------|-------------|
| GET | `/tareas` | Lista todas las tareas |
| GET | `/tareas/{id}` | Obtiene una tarea por ID |
| POST | `/tareas` | Crea una nueva tarea |
| PUT | `/tareas/{id}` | Actualiza una tarea |
| DELETE | `/tareas/{id}` | Elimina una tarea |

## Arquitectura

```
Cliente HTTP
    ↓
API Gateway (HTTP API v2)
    ↓
Lambda TareasApi (.NET 8)
    ↓              ↓
PostgreSQL       SQS Queue
(Supabase)           ↓
                Lambda TareasNotificacion
                (procesa notificaciones en background)
```

## Variables de entorno

| Variable | Descripción | Origen |
|----------|-------------|--------|
| `DB_CONNECTION` | Connection string PostgreSQL | SSM Parameter Store `/tareas-api/db-connection` |
| `SQS_URL` | URL de la cola SQS | SSM Parameter Store `/tareas-api/sqs-url` |

## Desarrollo local

### Prerequisitos

- .NET 8 SDK
- AWS CLI configurado (`aws configure`)
- Amazon.Lambda.Tools (`dotnet tool install -g Amazon.Lambda.Tools`)

### Compilar

```bash
dotnet build
```

### Deploy manual

```bash
dotnet lambda deploy-function
```

El nombre de la función y configuración se leen desde `aws-lambda-tools-defaults.json`.

## CI/CD

Cada push a `main` ejecuta el pipeline en GitHub Actions:

1. Checkout del código
2. Setup .NET 8
3. Configurar credenciales AWS
4. `dotnet build`
5. `dotnet lambda deploy-function`

Requiere los siguientes secrets en GitHub:
- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`

## Observabilidad

Los logs se escriben en formato JSON estructurado con los campos:

```json
{
  "timestamp": "2026-03-24T16:28:40Z",
  "level": "INFO",
  "service": "TareasApi",
  "requestId": "b825dd44-...",
  "message": "Request completado",
  "method": "GET",
  "path": "/tareas",
  "statusCode": 200,
  "durationMs": 45
}
```

Los logs están disponibles en CloudWatch Logs en el grupo `/aws/lambda/TareasApi`.

### Queries útiles en Logs Insights

```sql
-- Todos los requests recientes
fields timestamp, method, path, statusCode, durationMs
| filter level = "INFO"
| sort timestamp desc
| limit 20

-- Solo errores
fields timestamp, method, path, message
| filter level = "ERROR"
| sort timestamp desc

-- Promedio de duración por endpoint
fields path, durationMs
| filter level = "INFO"
| stats avg(durationMs) as avgMs, count() as total by path
| sort avgMs desc
```

## Estructura del proyecto

```
TareasApi/
├── .github/
│   └── workflows/
│       └── deploy.yml          # Pipeline CI/CD
├── Function.cs                 # Handler principal + CRUD
├── TareasApi.csproj            # Proyecto .NET
├── aws-lambda-tools-defaults.json  # Config deploy Lambda
└── global.json                 # Fuerza .NET 8
```

## Notas

- El cold start en .NET puede tardar 2-5 segundos en la primera invocación después de inactividad.
- La conexión a Supabase usa el connection pooler en modo Transaction (puerto 6543) para compatibilidad con el modelo serverless.
- El timeout de Lambda está configurado en 60 segundos para absorber cold starts y latencia de BD.
