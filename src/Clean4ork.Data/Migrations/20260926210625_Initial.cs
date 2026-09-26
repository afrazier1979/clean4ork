using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Clean4ork.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "Establishments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Jurisdiction = table.Column<string>(type: "text", nullable: false),
                    SourceFacilityId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    AddressLine1 = table.Column<string>(type: "text", nullable: true),
                    City = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: true),
                    PostalCode = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    FacilityType = table.Column<string>(type: "text", nullable: true),
                    BusinessEntityName = table.Column<string>(type: "text", nullable: true),
                    Location = table.Column<Point>(type: "geometry(Point, 4326)", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DataTier = table.Column<string>(type: "text", nullable: false),
                    SnapshotInspectionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SnapshotPassed = table.Column<bool>(type: "boolean", nullable: true),
                    SnapshotInspectionReason = table.Column<string>(type: "text", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastScrapedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Establishments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InspectionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EstablishmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    PreviousGrade = table.Column<char>(type: "character(1)", nullable: true),
                    NewGrade = table.Column<char>(type: "character(1)", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AlertedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionEvents_Establishments_EstablishmentId",
                        column: x => x.EstablishmentId,
                        principalTable: "Establishments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Inspections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EstablishmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInspectionId = table.Column<string>(type: "text", nullable: false),
                    InspectionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    InspectorName = table.Column<string>(type: "text", nullable: true),
                    SummaryNotes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Inspections_Establishments_EstablishmentId",
                        column: x => x.EstablishmentId,
                        principalTable: "Establishments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Violations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OrdinanceCitation = table.Column<string>(type: "text", nullable: true),
                    Observation = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Violations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Violations_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Establishments_DataTier",
                table: "Establishments",
                column: "DataTier");

            migrationBuilder.CreateIndex(
                name: "IX_Establishments_Jurisdiction_Slug",
                table: "Establishments",
                columns: new[] { "Jurisdiction", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Establishments_Jurisdiction_SourceFacilityId",
                table: "Establishments",
                columns: new[] { "Jurisdiction", "SourceFacilityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Establishments_PostalCode",
                table: "Establishments",
                column: "PostalCode");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionEvents_AlertedAt",
                table: "InspectionEvents",
                column: "AlertedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionEvents_EstablishmentId",
                table: "InspectionEvents",
                column: "EstablishmentId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionEvents_OccurredAt",
                table: "InspectionEvents",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_EstablishmentId_SourceInspectionId",
                table: "Inspections",
                columns: new[] { "EstablishmentId", "SourceInspectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_InspectionDate",
                table: "Inspections",
                column: "InspectionDate");

            migrationBuilder.CreateIndex(
                name: "IX_Violations_Code",
                table: "Violations",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_Violations_InspectionId",
                table: "Violations",
                column: "InspectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InspectionEvents");

            migrationBuilder.DropTable(
                name: "Violations");

            migrationBuilder.DropTable(
                name: "Inspections");

            migrationBuilder.DropTable(
                name: "Establishments");
        }
    }
}
