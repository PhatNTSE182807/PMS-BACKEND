using System.Net;
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
    IEmailService emailService,
    PmsDbContext pmsContext = null)
    : IUserService
{
    private const string LoginOtpPurpose = "LoginOtp";

    private const string InvalidLoginOtpMessage = "Login code is invalid or has expired";

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

        // Same status as a wrong password so the response doesn't reveal which emails are registered
        if (user == null)
            throw new BadRequestException("Email or password is incorrect");

        var signInResult = await signInManager.PasswordSignInAsync(user, loginUserModel.Password, false, false);

        if (!signInResult.Succeeded)
            throw new BadRequestException("Email or password is incorrect");

        return BuildLoginResponse(user);
    }

    public async Task RequestLoginOtpAsync(RequestLoginOtpModel model)
    {
        var user = await userManager.FindByEmailAsync(model.Email.Trim());

        // Unknown or locked-out accounts get the same response, so the endpoint doesn't reveal registered emails
        if (user == null || await userManager.IsLockedOutAsync(user))
            return;

        var code = await userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, LoginOtpPurpose);

        var body = $"""
            <p>Hello {WebUtility.HtmlEncode(user.UserName)},</p>
            <p>Your Parking Management System login code is:</p>
            <p style="font-size:28px;font-weight:bold;letter-spacing:6px">{code}</p>
            <p>The code is valid for a few minutes and can be used only once.
            If you did not request it, you can ignore this email.</p>
            """;

        await emailService.SendEmailAsync(EmailMessage.Create(user.Email, body, "Your login code"));
    }

    public async Task<LoginResponseModel> LoginWithOtpAsync(VerifyLoginOtpModel model)
    {
        var user = await userManager.FindByEmailAsync(model.Email.Trim());

        if (user == null)
            throw new BadRequestException(InvalidLoginOtpMessage);

        if (await userManager.IsLockedOutAsync(user))
            throw new BadRequestException("Too many failed attempts. Please try again in a few minutes.");

        var isValid = await userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, LoginOtpPurpose, model.Code.Trim());

        if (!isValid)
        {
            await userManager.AccessFailedAsync(user);
            throw new BadRequestException(InvalidLoginOtpMessage);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        // Codes are derived from the security stamp, so rotating it makes this code single-use
        await userManager.UpdateSecurityStampAsync(user);

        return BuildLoginResponse(user);
    }

    private LoginResponseModel BuildLoginResponse(ApplicationUser user)
    {
        return new LoginResponseModel
        {
            UserId = user.Id,
            Username = user.UserName,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            Role = user.Role,
            Token = JwtHelper.GenerateToken(user, configuration)
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
