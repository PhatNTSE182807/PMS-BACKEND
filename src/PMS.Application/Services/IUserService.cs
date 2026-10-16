using System.Collections.Generic;
using System.Threading.Tasks;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;

namespace PMS.Application.Services;

public interface IUserService
{
    Task<CreateUserResponseModel> CreateAsync(CreateUserModel createUserModel);

    Task<CreateUserResponseModel> CreateInternalUserAsync(CreateInternalUserModel createInternalUserModel);

    Task<CreateUserResponseModel> CreateManagerAsync(CreateEmployeeAccountModel model);

    Task<CreateUserResponseModel> CreateStaffAsync(CreateEmployeeAccountModel model);

    Task<LoginResponseModel> LoginAsync(LoginUserModel loginUserModel);

    Task RequestLoginOtpAsync(RequestLoginOtpModel model);

    Task<LoginResponseModel> LoginWithOtpAsync(VerifyLoginOtpModel model);

    Task<LoginResponseModel> LoginWithGoogleAsync(GoogleLoginModel model);

    Task<IEnumerable<UserResponseModel>> GetAllAsync(string role = null, bool? isActive = null, string searchTerm = null);

    Task<IEnumerable<UserResponseModel>> GetInternalUsersAsync(string role = null, bool? isActive = null, string searchTerm = null);

    Task<UserResponseModel> GetByIdAsync(string id);

    Task<bool> UpdateUserStatusAsync(string currentUserId, string targetUserId, bool isActive);

    Task<LoginResponseModel> VerifyTwoFactorAsync(TwoFactorLoginModel model);

    Task<ResendTwoFactorResponseModel> ResendTwoFactorOtpAsync(ResendTwoFactorModel model);
}
