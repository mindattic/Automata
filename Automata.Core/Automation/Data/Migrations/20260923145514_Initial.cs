using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Automata.Core.Automation.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    TaskOrder = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Settings_AllowLlmRepair = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_BrowserProfile = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_ContinueOnStepError = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_ContinueOnTaskError = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_DefaultStepTimeoutMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Settings_Discriminator = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_LlmProvider = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_ScreenshotOnFailure = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_SelfHeal = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_Retry_BackoffMultiplier = table.Column<double>(type: "REAL", nullable: true),
                    Settings_Retry_DelayMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Settings_Retry_MaxAttempts = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Datasets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Columns = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Datasets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Meta",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Meta", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ParkedRuns",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskName = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionId = table.Column<string>(type: "TEXT", nullable: false),
                    RemainingTaskIds = table.Column<string>(type: "TEXT", nullable: false),
                    TasksPassed = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTasks = table.Column<int>(type: "INTEGER", nullable: false),
                    ParkedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResumeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Checkpoint = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkedRuns", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.RunId);
                });

            migrationBuilder.CreateTable(
                name: "Schedule",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    Triggers = table.Column<string>(type: "TEXT", nullable: false),
                    NextDueUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastOutcome = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedule", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    BorderRadius = table.Column<int>(type: "INTEGER", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    SidebarWidth = table.Column<double>(type: "REAL", nullable: false),
                    PanelDetached = table.Column<bool>(type: "INTEGER", nullable: false),
                    PanelWindowLeft = table.Column<double>(type: "REAL", nullable: true),
                    PanelWindowTop = table.Column<double>(type: "REAL", nullable: true),
                    PanelWindowWidth = table.Column<double>(type: "REAL", nullable: false),
                    PanelWindowHeight = table.Column<double>(type: "REAL", nullable: false),
                    EngineDefaults_AllowLlmRepair = table.Column<bool>(type: "INTEGER", nullable: true),
                    EngineDefaults_BrowserProfile = table.Column<string>(type: "TEXT", nullable: true),
                    EngineDefaults_ContinueOnStepError = table.Column<bool>(type: "INTEGER", nullable: true),
                    EngineDefaults_ContinueOnTaskError = table.Column<bool>(type: "INTEGER", nullable: true),
                    EngineDefaults_DefaultStepTimeoutMs = table.Column<int>(type: "INTEGER", nullable: true),
                    EngineDefaults_Discriminator = table.Column<string>(type: "TEXT", nullable: true),
                    EngineDefaults_LlmProvider = table.Column<string>(type: "TEXT", nullable: true),
                    EngineDefaults_ScreenshotOnFailure = table.Column<bool>(type: "INTEGER", nullable: true),
                    EngineDefaults_SelfHeal = table.Column<bool>(type: "INTEGER", nullable: true),
                    EngineDefaults_Retry_BackoffMultiplier = table.Column<double>(type: "REAL", nullable: true),
                    EngineDefaults_Retry_DelayMs = table.Column<int>(type: "INTEGER", nullable: true),
                    EngineDefaults_Retry_MaxAttempts = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    StartUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Inputs = table.Column<string>(type: "TEXT", nullable: false),
                    Outputs = table.Column<string>(type: "TEXT", nullable: false),
                    Steps = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Demo_FactoryHash = table.Column<string>(type: "TEXT", nullable: true),
                    Demo_Key = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_AllowLlmRepair = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_BrowserProfile = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_ContinueOnStepError = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_ContinueOnTaskError = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_DefaultStepTimeoutMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Settings_Discriminator = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_LlmProvider = table.Column<string>(type: "TEXT", nullable: true),
                    Settings_ScreenshotOnFailure = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_SelfHeal = table.Column<bool>(type: "INTEGER", nullable: true),
                    Settings_Retry_BackoffMultiplier = table.Column<double>(type: "REAL", nullable: true),
                    Settings_Retry_DelayMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Settings_Retry_MaxAttempts = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatasetRows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DatasetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatasetRows_Datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunEvents_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunOutputs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    StepId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunOutputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunOutputs_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatasetRows_DatasetId_Ordinal",
                table: "DatasetRows",
                columns: new[] { "DatasetId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_Name",
                table: "Datasets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunEvents_RunId_TaskId",
                table: "RunEvents",
                columns: new[] { "RunId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunOutputs_RunId_TaskId",
                table: "RunOutputs",
                columns: new[] { "RunId", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_StartedUtc",
                table: "Runs",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CollectionId",
                table: "Tasks",
                column: "CollectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatasetRows");

            migrationBuilder.DropTable(
                name: "Meta");

            migrationBuilder.DropTable(
                name: "ParkedRuns");

            migrationBuilder.DropTable(
                name: "RunEvents");

            migrationBuilder.DropTable(
                name: "RunOutputs");

            migrationBuilder.DropTable(
                name: "Schedule");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Datasets");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Collections");
        }
    }
}
