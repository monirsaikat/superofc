using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Loans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Loan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PrincipalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    InterestMethod = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AnnualRate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false),
                    InstallmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InterestMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallmentAmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    StartYear = table.Column<int>(type: "INTEGER", nullable: false),
                    StartMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RequestedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DisbursedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DisbursementReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ClosedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
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
                    table.PrimaryKey("PK_Loan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loan_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoanInstallment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    PrincipalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    InterestMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PaidMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PayslipId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CarriedFromId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanInstallment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanInstallment_Loan_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanTransaction",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PayslipId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanTransaction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanTransaction_Loan_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoanAllocation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanAllocation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanAllocation_LoanInstallment_InstallmentId",
                        column: x => x.InstallmentId,
                        principalTable: "LoanInstallment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanAllocation_LoanTransaction_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "LoanTransaction",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Loan_CompanyId",
                table: "Loan",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Loan_CompanyId_Number",
                table: "Loan",
                columns: new[] { "CompanyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Loan_CompanyId_Status",
                table: "Loan",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Loan_EmployeeId",
                table: "Loan",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanAllocation_InstallmentId",
                table: "LoanAllocation",
                column: "InstallmentId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanAllocation_TransactionId",
                table: "LoanAllocation",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanInstallment_LoanId_Sequence",
                table: "LoanInstallment",
                columns: new[] { "LoanId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanInstallment_PayslipId",
                table: "LoanInstallment",
                column: "PayslipId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanInstallment_Year_Month_Status",
                table: "LoanInstallment",
                columns: new[] { "Year", "Month", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanTransaction_LoanId_Date",
                table: "LoanTransaction",
                columns: new[] { "LoanId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanTransaction_PayslipId",
                table: "LoanTransaction",
                column: "PayslipId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanAllocation");

            migrationBuilder.DropTable(
                name: "LoanInstallment");

            migrationBuilder.DropTable(
                name: "LoanTransaction");

            migrationBuilder.DropTable(
                name: "Loan");
        }
    }
}
