using PMS.Application.Models;
using PMS.Application.Models.User;

namespace PMS.Application.Services;

public interface IUserService
{
    Task<CreateUserResponseModel> CreateAsync(CreateUserModel createUserModel);

    Task<LoginResponseModel> LoginAsync(LoginUserModel loginUserModel);

    Task<IEnumerable<UserResponseModel>> GetAllAsync();

    Task<UserResponseModel> GetByIdAsync(string id);
}
