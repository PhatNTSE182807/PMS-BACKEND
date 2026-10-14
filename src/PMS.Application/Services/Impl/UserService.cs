using MapsterMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PMS.Application.Common.Email;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.User;
using PMS.Application.Templates;
using PMS.DataAccess.Identity;

using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class UserService(
    IMapper mapper,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IConfiguration configuration,
    PmsDbContext pmsContext = null)
    : IUserService
{
    public async Task<CreateUserResponseModel> CreateAsync(CreateUserModel createUserModel)
    {
        var user = mapper.Map<ApplicationUser>(createUserModel);
        user.PhoneNumber = createUserModel.PhoneNumber;
        user.EmailConfirmed = true;
        user.Role = "User";

        var result = await userManager.CreateAsync(user, createUserModel.Password);

        if (!result.Succeeded) throw new BadRequestException(result.Errors.FirstOrDefault()?.Description);

        if (pmsContext != null)
        {
            var customer = new Customer
            {
                UserId = user.Id,
                CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                FullName = !string.IsNullOrWhiteSpace(createUserModel.FullName) ? createUserModel.FullName.Trim() : (user.UserName ?? "User"),
                Phone = user.PhoneNumber,
                Email = user.Email,
                CustomerType = "Individual",
                MembershipTier = "Standard",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            pmsContext.Customers.Add(customer);
            await pmsContext.SaveChangesAsync();
        }

        return new CreateUserResponseModel
        {
            Id = Guid.Parse(user.Id)
        };
    }

    public async Task<LoginResponseModel> LoginAsync(LoginUserModel loginUserModel)
    {
        var user = await userManager.FindByEmailAsync(loginUserModel.Email.Trim());

        if (user == null)
            throw new NotFoundException("Email or password is incorrect");

        var signInResult = await signInManager.PasswordSignInAsync(user, loginUserModel.Password, false, false);

        if (!signInResult.Succeeded)
            throw new BadRequestException("Email or password is incorrect");

        var token = JwtHelper.GenerateToken(user, configuration);

        return new LoginResponseModel
        {
            UserId = user.Id,
            Username = user.UserName,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            Role = user.Role,
            Token = token
        };
    }

    public async Task<IEnumerable<UserResponseModel>> GetAllAsync()
    {
        return await userManager.Users
            .Select(u => new UserResponseModel
            {
                Id = u.Id,
                UserName = u.UserName,
                Email = u.Email,
                PhoneNumber = u.PhoneNumber,
                Role = u.Role
            })
            .ToListAsync();
    }

    public async Task<UserResponseModel> GetByIdAsync(string id)
    {
        var user = await userManager.FindByIdAsync(id);

        if (user == null)
            throw new NotFoundException("User does not exist");

        return new UserResponseModel
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            Role = user.Role
        };
    }
}
