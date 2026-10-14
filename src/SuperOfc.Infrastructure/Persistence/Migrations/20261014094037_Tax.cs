using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Tax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmployeeTaxProfile",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalYear = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DeclaredInvestmentMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PriorEmployerIncomeMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PriorEmployerTaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IsExempt = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeTaxProfile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeTaxProfile_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxRuleSet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    FiscalYear = table.Column<int>(type: "INTEGER", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RulesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRuleSet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxComputation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayslipId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleSetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FiscalYear = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxableThisMonthMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ProjectedIncomeMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ExemptMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxableIncomeMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AnnualTaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PaidBeforeMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DeductedMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ExplanationJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxComputation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxComputation_Payslip_PayslipId",
                        column: x => x.PayslipId,
                        principalTable: "Payslip",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaxComputation_TaxRuleSet_RuleSetId",
                        column: x => x.RuleSetId,
                        principalTable: "TaxRuleSet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeTaxProfile_CompanyId",
                table: "EmployeeTaxProfile",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeTaxProfile_EmployeeId_FiscalYear",
                table: "EmployeeTaxProfile",
                columns: new[] { "EmployeeId", "FiscalYear" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxComputation_CompanyId",
                table: "TaxComputation",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxComputation_EmployeeId_FiscalYear",
                table: "TaxComputation",
                columns: new[] { "EmployeeId", "FiscalYear" });

            migrationBuilder.CreateIndex(
                name: "IX_TaxComputation_PayslipId",
                table: "TaxComputation",
                column: "PayslipId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxComputation_RuleSetId",
                table: "TaxComputation",
                column: "RuleSetId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRuleSet_CompanyId",
                table: "TaxRuleSet",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRuleSet_CompanyId_FiscalYear_EffectiveFrom",
                table: "TaxRuleSet",
                columns: new[] { "CompanyId", "FiscalYear", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeTaxProfile");

            migrationBuilder.DropTable(
                name: "TaxComputation");

            migrationBuilder.DropTable(
                name: "TaxRuleSet");
        }
    }
}
