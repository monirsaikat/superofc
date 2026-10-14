using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Payroll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PayrollAdjustment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IsTaxable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdjustment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollAdjustment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayrollPeriod",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollPeriod", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalaryAssignment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    StructureId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalaryAssignment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SalaryComponent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Calc = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DefaultRate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false),
                    DefaultAmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Formula = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    IsBasic = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTaxable = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAttendanceLinked = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryComponent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalaryStructure",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryStructure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ComputedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FinalizedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinalizedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VoidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PolicyJson = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    EmployeeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    GrossMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DeductionsMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    NetMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    EmployerContributionsMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollRun_PayrollPeriod_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "PayrollPeriod",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalaryAssignmentLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Rate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryAssignmentLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalaryAssignmentLine_SalaryAssignment_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "SalaryAssignment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SalaryAssignmentLine_SalaryComponent_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "SalaryComponent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalaryStructureLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StructureId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Rate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryStructureLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalaryStructureLine_SalaryComponent_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "SalaryComponent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalaryStructureLine_SalaryStructure_StructureId",
                        column: x => x.StructureId,
                        principalTable: "SalaryStructure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payslip",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EmployeeName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Department = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Designation = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PaymentMode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BankName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BankAccount = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CalendarDays = table.Column<int>(type: "INTEGER", nullable: false),
                    PayableDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    PresentDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    HalfDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    PaidLeaveDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    UnpaidLeaveDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    AbsentDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    Holidays = table.Column<int>(type: "INTEGER", nullable: false),
                    WeeklyOffs = table.Column<int>(type: "INTEGER", nullable: false),
                    LateDays = table.Column<int>(type: "INTEGER", nullable: false),
                    LatePenaltyDays = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    OvertimeHours = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    GrossMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DeductionsMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    NetMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    LoanMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    EmployerContributionsMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxableGrossMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Warnings = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PaymentReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payslip", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payslip_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payslip_PayrollRun_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayslipLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayslipId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Basis = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    FullAmountMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IsTaxable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayslipLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayslipLine_Payslip_PayslipId",
                        column: x => x.PayslipId,
                        principalTable: "Payslip",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustment_CompanyId",
                table: "PayrollAdjustment",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustment_CompanyId_Year_Month",
                table: "PayrollAdjustment",
                columns: new[] { "CompanyId", "Year", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustment_EmployeeId",
                table: "PayrollAdjustment",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriod_CompanyId",
                table: "PayrollPeriod",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollPeriod_CompanyId_Year_Month",
                table: "PayrollPeriod",
                columns: new[] { "CompanyId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRun_CompanyId",
                table: "PayrollRun",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRun_PeriodId_RunNumber",
                table: "PayrollRun",
                columns: new[] { "PeriodId", "RunNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payslip_CompanyId",
                table: "Payslip",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Payslip_EmployeeId",
                table: "Payslip",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Payslip_RunId_EmployeeId",
                table: "Payslip",
                columns: new[] { "RunId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayslipLine_PayslipId",
                table: "PayslipLine",
                column: "PayslipId");

            migrationBuilder.CreateIndex(
                name: "IX_PayslipLine_ReferenceId",
                table: "PayslipLine",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAssignment_CompanyId",
                table: "SalaryAssignment",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAssignment_EmployeeId_EffectiveFrom",
                table: "SalaryAssignment",
                columns: new[] { "EmployeeId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAssignmentLine_AssignmentId",
                table: "SalaryAssignmentLine",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryAssignmentLine_ComponentId",
                table: "SalaryAssignmentLine",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryComponent_CompanyId",
                table: "SalaryComponent",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryComponent_CompanyId_Code",
                table: "SalaryComponent",
                columns: new[] { "CompanyId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructure_CompanyId",
                table: "SalaryStructure",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructureLine_ComponentId",
                table: "SalaryStructureLine",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryStructureLine_StructureId",
                table: "SalaryStructureLine",
                column: "StructureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayrollAdjustment");

            migrationBuilder.DropTable(
                name: "PayslipLine");

            migrationBuilder.DropTable(
                name: "SalaryAssignmentLine");

            migrationBuilder.DropTable(
                name: "SalaryStructureLine");

            migrationBuilder.DropTable(
                name: "Payslip");

            migrationBuilder.DropTable(
                name: "SalaryAssignment");

            migrationBuilder.DropTable(
                name: "SalaryComponent");

            migrationBuilder.DropTable(
                name: "SalaryStructure");

            migrationBuilder.DropTable(
                name: "PayrollRun");

            migrationBuilder.DropTable(
                name: "PayrollPeriod");
        }
    }
}
