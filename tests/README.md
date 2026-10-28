# Backend tests

| Project | Kind |
| --- | --- |
| `PMS.Application.UnitTests` | Unit tests for services and validators (xUnit, NSubstitute). |
| `PMS.Api.IntegrationTests` | API tests against a running app (`WebApplicationFactory`). |

Both projects reference `coverlet.collector`, so coverage works with the standard data collector.

## Run the unit tests with coverage

From the repository root:

```sh
dotnet test tests/PMS.Application.UnitTests --settings coverlet.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults
```

The run writes `TestResults/<guid>/coverage.cobertura.xml`. The overall line coverage is the `line-rate` attribute on the root `<coverage>` element.

`coverlet.runsettings` measures only the `PMS.*` assemblies and skips the test projects, EF Core migrations, generated code, auto-properties and start-up wiring that unit tests cannot run: `*DependencyInjection.cs`, the migration runner (`AutomatedMigration*`), the local SQL Server probe (`DatabaseConnectionStringResolver`) and the Development demo seed (`Helpers/DemoData`, covered by the integration tests).

## HTML report (optional)

```sh
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:"TestResults/*/coverage.cobertura.xml" -targetdir:TestResults/coverage-report -reporttypes:"Html;TextSummary"
```

Open `TestResults/coverage-report/index.html`. Delete `TestResults/` before a new run if you only want the latest result in the report.
