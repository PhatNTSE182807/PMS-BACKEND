namespace PMS.Application.Models.Profile;

public class VehicleTypeResponseModel
{
    public int Id { get; set; }

    public string Code { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SortOrder { get; set; }
}
