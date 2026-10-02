using N_Tier.Application.Models;
using N_Tier.Application.Models.User;

namespace N_Tier.Application.Services;

public interface IUserService
{
    Task<CreateUserResponseModel> CreateAsync(CreateUserModel createUserModel);

    Task<LoginResponseModel> LoginAsync(LoginUserModel loginUserModel);

    Task<IEnumerable<UserResponseModel>> GetAllAsync();

    Task<UserResponseModel> GetByIdAsync(string id);
}
