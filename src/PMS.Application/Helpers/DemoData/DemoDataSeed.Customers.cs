using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Models.VehicleDocuments;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Helpers.DemoData;

/// <summary>Demo customers, their vehicles and paper checks, and the lots' plate lists.</summary>
public sealed partial class DemoDataSeed
{
    private sealed record CustomerDef(
        string Key, string UserName, string FullName, string Phone, string CitizenId, DateOnly? BirthDate, string Address,
        int JoinedDaysAgo, bool PaysWithWallet);

    private static readonly CustomerDef[] CustomerDefs =
    [
        new("an", "an.nguyen", "Nguyễn Văn An", "0912000301", "079098001234", new DateOnly(1998, 3, 12),
            "12 Vo Van Ngan Street, Thu Duc City, Ho Chi Minh City", 75, true),
        new("binh", "binh.tran", "Trần Thị Bình", "0912000302", "079199002345", new DateOnly(1999, 7, 21),
            "45 Nguyen Van Troi Street, Phu Nhuan District, Ho Chi Minh City", 60, true),
        new("cuong", "cuong.le", "Lê Hoàng Cường", "0912000303", "079097003456", new DateOnly(1997, 11, 2),
            "88 Le Van Viet Street, Thu Duc City, Ho Chi Minh City", 3, false),
        new("dung", "dung.pham", "Phạm Thu Dung", "0912000304", "048196004567", new DateOnly(1996, 1, 15),
            "23 Bach Dang Street, Hai Chau District, Da Nang", 25, false),
        new("em", "em.vo", "Võ Minh Em", "0912000305", "079200005678", new DateOnly(2000, 5, 30),
            "7 Cong Hoa Street, Tan Binh District, Ho Chi Minh City", 45, false),
        new("giang", "giang.hoang", "Hoàng Gia Giang", "0912000306", "001095006789", new DateOnly(1995, 9, 9),
            "102 Truong Chinh Street, Tan Binh District, Ho Chi Minh City", 40, true),
        new("hai", "hai.do", "Đỗ Thanh Hải", "0912000307", "079094007890", new DateOnly(1994, 12, 24),
            "5 Nguyen Hue Boulevard, District 1, Ho Chi Minh City", 70, true),
        // A brand-new account: no Citizen ID yet, so it cannot add a vehicle until the profile is filled in
        new("khoa", "khoa.mai", "Mai Đăng Khoa", "0912000308", null, null, null, 0, false)
    ];

    private enum Paper
    {
        Approved,
        NeedsReview,
        Draft,
        Rejected
    }

    /// <param name="PaperOwner">Owner printed on the registration certificate when it differs from the account (Staff should reject).</param>
    /// <param name="PaperPlate">Plate printed on the certificate when it differs from the one entered.</param>
    private sealed record VehicleDef(
        string CustomerKey, string Plate, string TypeCode, string Brand, string Model, string Color, Paper Paper, double AddedDaysAgo,
        string PaperOwner = null, string PaperPlate = null, string ReviewerUserName = "staff.fptu1");

    private static readonly VehicleDef[] VehicleDefs =
    [
        new("an", "51K-123.45", Car4, "Toyota", "Vios", "White", Paper.Approved, 62),
        new("an", "51H-678.90", Car7, "Mitsubishi", "Xpander", "Silver", Paper.Approved, 50, ReviewerUserName: "staff.fsoft1"),
        new("an", "59X1-234.56", Motorbike, "Honda", "Air Blade", "Black", Paper.Approved, 55),
        new("binh", "30G-555.88", Car4, "Mazda", "3", "Red", Paper.Approved, 45, ReviewerUserName: "staff.fsoft1"),
        new("cuong", "51F-246.80", Car4, "Hyundai", "Accent", "White", Paper.NeedsReview, 0.9),
        new("cuong", "59T2-135.79", Motorbike, "Yamaha", "Exciter", "Blue", Paper.Draft, 0.2),
        new("dung", "29A-111.22", Car4, "VinFast", "Fadil", "Grey", Paper.Rejected, 9, PaperPlate: "29A-111.32", ReviewerUserName: "staff.conghoa1"),
        new("dung", "43A-246.13", Car4, "Kia", "Morning", "Yellow", Paper.Approved, 20, ReviewerUserName: "staff.conghoa1"),
        new("dung", "43A-777.99", Car7, "Toyota", "Fortuner", "Black", Paper.NeedsReview, 1.6, PaperOwner: "Trần Văn Hùng"),
        new("em", "51G-999.01", Car4, "Honda", "City", "Blue", Paper.Approved, 40),
        new("giang", "51L-202.02", Car4, "Ford", "Focus", "Orange", Paper.Approved, 35, ReviewerUserName: "staff.conghoa1"),
        new("hai", "59C1-468.02", Motorbike, "Honda", "Vision", "Pink", Paper.Approved, 66, ReviewerUserName: "staff.fsoft2"),
        new("hai", "51A-864.20", Car4, "Toyota", "Corolla Cross", "White", Paper.Approved, 64, ReviewerUserName: "staff.fsoft2")
    ];

    /// <summary>Plates of walk-in vehicles parked right now (no account).</summary>
    private static readonly string[] GuestPlatesInside =
        ["51D-357.91", "51B-468.24", "59P1-111.11", "51A-000.01", "61A-135.24", "51E-246.80", "59T1-777.77"];

    private sealed record PlateListDef(string ListType, string Plate, string LotKey, string Reason, int ExpiresInDays, string CreatedByUserName);

    private static readonly PlateListDef[] PlateListDefs =
    [
        new(PlateListType.Whitelist, "51A-000.01", "fptu", "FPT University shuttle car", 365, "mgr.fptu"),
        new(PlateListType.Whitelist, "80A-000.99", null, "Emergency service vehicle", 365, null),
        new(PlateListType.Whitelist, "51C-999.99", "conghoa", "Wedding guest at the hall next door", -2, "mgr.conghoa"),
        new(PlateListType.Blacklist, "51K-666.66", null, "Left without paying three times", 180, null),
        new(PlateListType.Blacklist, "30E-404.04", "fsoft", "Reported stolen by the owner", 90, "mgr.fsoft")
    ];

    /// <summary>Demo plates already used by someone else's vehicle or by a vehicle parked now: the parking story is skipped then.</summary>
    private async Task<List<string>> PlatesInUseAsync()
    {
        var demoCustomerIds = customers.Values.Select(c => c.Customer.Id).ToList();
        var vehiclePlates = VehicleDefs.Select(v => ParkingText.NormalizePlate(v.Plate)).ToList();
        var takenByOthers = await db.Vehicles
            .Where(v => v.IsActive && vehiclePlates.Contains(v.PlateNormalized) && (v.CustomerId == null || !demoCustomerIds.Contains(v.CustomerId.Value)))
            .Select(v => v.PlateNumber)
            .ToListAsync();

        var parkedPlates = vehiclePlates.Concat(GuestPlatesInside.Select(ParkingText.NormalizePlate)).ToList();
        var parked = await db.ParkingSessions
            .Where(s => SessionStatus.Open.Contains(s.Status) && s.HasPlate && parkedPlates.Contains(s.EntryPlateNormalized))
            .Select(s => s.EntryPlateNumber)
            .ToListAsync();

        // Demo customers that already have vehicles were set up by hand: leave them alone
        var ownVehicles = await db.Vehicles.Where(v => v.CustomerId != null && demoCustomerIds.Contains(v.CustomerId.Value))
            .Select(v => v.PlateNumber).ToListAsync();

        return takenByOthers.Concat(parked).Concat(ownVehicles).Distinct().ToList();
    }

    private async Task SeedPlateListsAsync()
    {
        foreach (var def in PlateListDefs)
        {
            usedPlates.Add(ParkingText.NormalizePlate(def.Plate));
            var createdAt = now.AddDays(-random.Next(10, 40));
            db.PlateLists.Add(new PlateListEntry
            {
                ListType = def.ListType,
                PlateNumber = def.Plate,
                PlateNormalized = ParkingText.NormalizePlate(def.Plate),
                ParkingLotId = def.LotKey == null ? null : lots[def.LotKey].Id,
                Reason = def.Reason,
                ExpiresAt = Trim(now.AddDays(def.ExpiresInDays)),
                IsActive = true,
                CreatedBy = def.CreatedByUserName == null ? adminId : employees[def.CreatedByUserName].Id,
                CreatedAt = createdAt
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Vehicles with their paper checks: approved (photos purged after 30 days), waiting for Staff, draft or rejected.</summary>
    private async Task SeedCustomerProfilesAsync()
    {
        foreach (var plate in GuestPlatesInside.Concat(VehicleDefs.Select(v => v.Plate)))
            usedPlates.Add(ParkingText.NormalizePlate(plate));

        foreach (var def in VehicleDefs)
        {
            var owner = customers[def.CustomerKey];
            var customer = owner.Customer;
            var addedAt = HoursAgo(def.AddedDaysAgo * 24);
            var isFirstActive = def.Paper != Paper.Rejected
                                && !await db.Vehicles.AnyAsync(v => v.CustomerId == customer.Id && v.IsActive);
            var vehicle = new Vehicle
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                VehicleTypeId = types[def.TypeCode].Id,
                PlateNumber = def.Plate,
                PlateNormalized = ParkingText.NormalizePlate(def.Plate),
                Brand = def.Brand,
                Model = def.Model,
                Color = def.Color,
                CitizenId = customer.CitizenId,
                IsDefault = isFirstActive,
                IsActive = def.Paper != Paper.Rejected,
                CreatedAt = addedAt,
                UpdatedAt = addedAt
            };
            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync();

            var verification = new VehicleVerification
            {
                VehicleId = vehicle.Id,
                CustomerId = customer.Id,
                UserId = owner.UserId,
                Status = VehicleVerificationStatus.Draft,
                CreatedAt = addedAt.AddMinutes(3)
            };
            db.VehicleVerifications.Add(verification);

            var submittedAt = addedAt.AddMinutes(9);
            switch (def.Paper)
            {
                case Paper.Draft:
                    // Only the registration certificate so far: the owner still has to add the Citizen ID photo and submit
                    await AddPaperAsync(verification, owner, def, vehicle, VehicleDocumentKinds.RegistrationCertificate, keepFile: true);
                    break;

                case Paper.NeedsReview:
                    verification.Status = VehicleVerificationStatus.NeedsReview;
                    verification.StatusReason = "Waiting for Staff to compare your papers with this vehicle.";
                    verification.SubmittedAt = submittedAt;
                    await AddPaperAsync(verification, owner, def, vehicle, VehicleDocumentKinds.RegistrationCertificate, keepFile: true);
                    await AddPaperAsync(verification, owner, def, vehicle, VehicleDocumentKinds.CitizenId, keepFile: true);
                    break;

                default:
                    var approved = def.Paper == Paper.Approved;
                    var decidedAt = submittedAt.AddHours(2 + random.Next(0, 18));
                    var keepFiles = decidedAt > now.AddDays(-documentRetentionDays);
                    verification.Status = approved ? VehicleVerificationStatus.Approved : VehicleVerificationStatus.Rejected;
                    verification.StatusReason = approved
                        ? "Papers checked by Staff. The vehicle can be booked and is recognised at the gates."
                        : "Staff rejected these papers, so the vehicle was removed from your account. Register it again with new photos.";
                    verification.SubmittedAt = submittedAt;
                    verification.ReviewedBy = employees[def.ReviewerUserName].Id;
                    verification.ReviewNote = approved ? null : "The plate on the registration certificate does not match the plate entered.";
                    verification.DecidedAt = decidedAt;
                    // BR-26: photos are deleted 30 days after the decision; the result stays
                    verification.FilesDeletedAt = keepFiles ? null : decidedAt.AddDays(documentRetentionDays);
                    await AddPaperAsync(verification, owner, def, vehicle, VehicleDocumentKinds.RegistrationCertificate, keepFiles);
                    await AddPaperAsync(verification, owner, def, vehicle, VehicleDocumentKinds.CitizenId, keepFiles);
                    decidedVerifications.Add(verification);
                    if (approved)
                        owner.Verified.Add((vehicle, decidedAt));
                    else
                        vehicle.UpdatedAt = decidedAt;
                    break;
            }

            await db.SaveChangesAsync();
        }
    }

    /// <summary>One uploaded paper; <paramref name="keepFile"/> false is a photo already purged (no file left).</summary>
    private async Task AddPaperAsync(VehicleVerification verification, CustomerInfo owner, VehicleDef def, Vehicle vehicle, string documentType, bool keepFile)
    {
        var customer = owner.Customer;
        var isCertificate = documentType == VehicleDocumentKinds.RegistrationCertificate;
        var lines = isCertificate
            ? new List<(string, string)>
            {
                ("Plate number", def.PaperPlate ?? def.Plate),
                ("Registered owner", (def.PaperOwner ?? customer.FullName).ToUpperInvariant()),
                ("Owner citizen ID", def.PaperOwner != null ? "079085009999" : customer.CitizenId),
                ("Brand / model", $"{def.Brand} {def.Model}"),
                ("Color", def.Color),
                ("Vehicle type", types[def.TypeCode].Name)
            }
            : new List<(string, string)>
            {
                ("ID number", customer.CitizenId),
                ("Full name", customer.FullName.ToUpperInvariant()),
                ("Date of birth", customer.DateOfBirth?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "-"),
                ("Place of residence", customer.Address ?? "-")
            };
        var bytes = SamplePaperPdf.Create(isCertificate ? "VEHICLE REGISTRATION CERTIFICATE" : "CITIZEN IDENTITY CARD", lines);

        string storagePath = null;
        if (keepFile)
        {
            using var content = new MemoryStream(bytes);
            storagePath = await storage.SaveAsync(content, verification.Id.ToString("N"));
        }

        var document = new VehicleDocument
        {
            VerificationId = verification.Id,
            DocumentType = documentType,
            Side = VehicleDocumentKinds.Front,
            StoragePath = storagePath,
            OriginalFileName = isCertificate ? $"registration-{vehicle.PlateNormalized.ToLowerInvariant()}.pdf" : "citizen-id.pdf",
            ContentType = "application/pdf",
            SizeBytes = bytes.Length,
            UploadedAt = verification.CreatedAt.AddMinutes(isCertificate ? 1 : 4)
        };
        // Added explicitly: the key is set client-side, so EF must not guess the entity already exists
        db.VehicleDocuments.Add(document);
        verification.Documents.Add(document);
    }

    /// <summary>The customer's vehicle with this plate.</summary>
    private Vehicle VehicleOf(CustomerInfo customer, string plate)
    {
        var normalized = ParkingText.NormalizePlate(plate);
        return db.Vehicles.Local.First(v => v.CustomerId == customer.Customer.Id && v.PlateNormalized == normalized);
    }
}
