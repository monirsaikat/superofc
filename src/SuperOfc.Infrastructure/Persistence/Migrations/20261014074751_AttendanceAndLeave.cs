using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SuperOfc.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AttendanceAndLeave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendanceCorrection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    AfterJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceCorrection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceCorrection_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceDay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FirstIn = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOut = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PunchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LateMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    EarlyLeaveMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    OvertimeMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    MissingPunch = table.Column<bool>(type: "INTEGER", nullable: false),
                    LeaveDays = table.Column<decimal>(type: "TEXT", precision: 4, scale: 1, nullable: false),
                    LeaveTypeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LeaveIsPaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsManuallyCorrected = table.Column<bool>(type: "INTEGER", nullable: false),
                    CorrectionNote = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ComputedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDay", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceDay_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttendancePunch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PunchAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LocalTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DeviceUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
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
                    table.PrimaryKey("PK_AttendancePunch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendancePunch_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaveType",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    IsPaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    YearlyEntitlement = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    Accrual = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CarryForwardCap = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    AllowsHalfDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresAttachment = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowNegativeBalance = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_LeaveType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shift",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    StartTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    GraceInMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    GraceOutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    BreakMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumMinutesForHalfDay = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumMinutesForFullDay = table.Column<int>(type: "INTEGER", nullable: false),
                    OvertimeStartsAfterMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_Shift", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeaveBalance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaveTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Opening = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    Accrued = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    Used = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    Adjusted = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveBalance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaveBalance_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaveBalance_LeaveType_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalTable: "LeaveType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaveRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaveTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ToDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsHalfDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    Days = table.Column<decimal>(type: "TEXT", precision: 6, scale: 1, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttachmentPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaveRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaveRequest_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaveRequest_LeaveType_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalTable: "LeaveType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShiftAssignment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RotationShiftIds = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RotationDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShiftAssignment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employee",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShiftAssignment_Shift_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shift",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceCorrection_CompanyId",
                table: "AttendanceCorrection",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceCorrection_EmployeeId_Date",
                table: "AttendanceCorrection",
                columns: new[] { "EmployeeId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDay_CompanyId",
                table: "AttendanceDay",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDay_CompanyId_Date",
                table: "AttendanceDay",
                columns: new[] { "CompanyId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDay_EmployeeId_Date",
                table: "AttendanceDay",
                columns: new[] { "EmployeeId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunch_CompanyId",
                table: "AttendancePunch",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunch_CompanyId_LocalTime",
                table: "AttendancePunch",
                columns: new[] { "CompanyId", "LocalTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunch_DedupKey",
                table: "AttendancePunch",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendancePunch_EmployeeId_LocalTime",
                table: "AttendancePunch",
                columns: new[] { "EmployeeId", "LocalTime" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveBalance_CompanyId",
                table: "LeaveBalance",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveBalance_EmployeeId_LeaveTypeId_Year",
                table: "LeaveBalance",
                columns: new[] { "EmployeeId", "LeaveTypeId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaveBalance_LeaveTypeId",
                table: "LeaveBalance",
                column: "LeaveTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequest_CompanyId",
                table: "LeaveRequest",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequest_CompanyId_Status",
                table: "LeaveRequest",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequest_EmployeeId_FromDate",
                table: "LeaveRequest",
                columns: new[] { "EmployeeId", "FromDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaveRequest_LeaveTypeId",
                table: "LeaveRequest",
                column: "LeaveTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveType_CompanyId",
                table: "LeaveType",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaveType_CompanyId_Code",
                table: "LeaveType",
                columns: new[] { "CompanyId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Shift_CompanyId",
                table: "Shift",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Shift_CompanyId_Name",
                table: "Shift",
                columns: new[] { "CompanyId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftAssignment_CompanyId",
                table: "ShiftAssignment",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftAssignment_EmployeeId_EffectiveFrom",
                table: "ShiftAssignment",
                columns: new[] { "EmployeeId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftAssignment_ShiftId",
                table: "ShiftAssignment",
                column: "ShiftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceCorrection");

            migrationBuilder.DropTable(
                name: "AttendanceDay");

            migrationBuilder.DropTable(
                name: "AttendancePunch");

            migrationBuilder.DropTable(
                name: "LeaveBalance");

            migrationBuilder.DropTable(
                name: "LeaveRequest");

            migrationBuilder.DropTable(
                name: "ShiftAssignment");

            migrationBuilder.DropTable(
                name: "LeaveType");

            migrationBuilder.DropTable(
                name: "Shift");
        }
    }
}
