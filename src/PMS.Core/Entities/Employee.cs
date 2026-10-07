using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Employee
{
    public int Id { get; set; }

    public string UserId { get; set; }

    public string EmployeeCode { get; set; }

    public string FullName { get; set; }

    public string Phone { get; set; }

    public string Email { get; set; }

    public string Position { get; set; }

    public int? ParkingLotId { get; set; }

    public DateOnly? HireDate { get; set; }

    public DateOnly? TerminationDate { get; set; }

    public string Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
