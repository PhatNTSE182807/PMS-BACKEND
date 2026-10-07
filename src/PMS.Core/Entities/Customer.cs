using System;
using System.Collections.Generic;

namespace PMS.DataAccess.Persistence.Entities;

public partial class Customer
{
    public int Id { get; set; }

    public string UserId { get; set; }

    public string CustomerCode { get; set; }

    public string FullName { get; set; }

    public string Phone { get; set; }

    public string Email { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public string Gender { get; set; }

    public string Address { get; set; }

    public string CustomerType { get; set; }

    public int? CorporateAccountId { get; set; }

    public int LoyaltyPoints { get; set; }

    public string MembershipTier { get; set; }

    public bool IsActive { get; set; }

    public DateTime? AnonymizedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<ParkingSession> ParkingSessions { get; set; } = new List<ParkingSession>();

    public virtual ICollection<Vehicle> Vehicles { get; set; } = new List<Vehicle>();
}
