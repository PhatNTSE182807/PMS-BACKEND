using System.Net;
using MapsterMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PMS.Application.Common.Email;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Models;
using PMS.Application.Models.Admin;
using PMS.Application.Models.User;
using PMS.DataAccess.Identity;

using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

public class UserService(
    IMapper mapper,
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IConfiguration configuration,
    PmsDbContext pmsContext = null,
    IEmailService emailService = null,
    IGoogleTokenValidator googleTokenValidator = null)
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

        var citizenId = createUserModel.CitizenId?.Trim();
        if (pmsContext != null && !string.IsNullOrEmpty(citizenId) &&
            await pmsContext.Customers.AnyAsync(c => c.CitizenId == citizenId))
            throw new BadRequestException("Citizen ID is already registered to another account");

        var result = await userManager.CreateAsync(user, createUserModel.Password);

        if (!result.Succeeded) throw new BadRequestException(result.Errors.FirstOrDefault()?.Description);

        if (pmsContext != null)
        {
            try
            {
                var customer = new Customer
                {
                    UserId = user.Id,
                    CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                    FullName = !string.IsNullOrWhiteSpace(createUserModel.FullName) ? createUserModel.FullName.Trim() : (user.UserName ?? "User"),
                    Phone = user.PhoneNumber,
                    Email = user.Email,
                    CitizenId = string.IsNullOrEmpty(citizenId) ? null : citizenId,
                    CustomerType = "Individual",
                    MembershipTier = "Standard",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                pmsContext.Customers.Add(customer);
                await pmsContext.SaveChangesAsync();
            }
            catch
            {
                await userManager.DeleteAsync(user);
                throw;
            }
        }

        return new CreateUserResponseModel
        {
            Id = Guid.Parse(user.Id)
        };
    }

    public async Task<CreateUserResponseModel> CreateInternalUserAsync(CreateInternalUserModel createInternalUserModel)
    {
        return await CreateInternalUserCoreAsync(
            createInternalUserModel.UserName,
            createInternalUserModel.FullName,
            createInternalUserModel.Email,
            createInternalUserModel.Password,
            createInternalUserModel.PhoneNumber,
            createInternalUserModel.Role);
    }

    public async Task<CreateUserResponseModel> CreateManagerAsync(CreateEmployeeAccountModel model)
    {
        return await CreateInternalUserCoreAsync(
            model.UserName,
            model.FullName,
            model.Email,
            model.Password,
            model.PhoneNumber,
            "Manager");
    }

    public async Task<CreateUserResponseModel> CreateStaffAsync(CreateEmployeeAccountModel model)
    {
        return await CreateInternalUserCoreAsync(
            model.UserName,
            model.FullName,
            model.Email,
            model.Password,
            model.PhoneNumber,
            "Staff");
    }

    private async Task<CreateUserResponseModel> CreateInternalUserCoreAsync(
        string userName,
        string fullName,
        string email,
        string password,
        string phoneNumber,
        string role)
    {
        var normalizedRole = string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase)
            ? "Manager"
            : string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase)
                ? "Staff"
                : throw new BadRequestException("Role must be either 'Manager' or 'Staff'");

        // The Admin sets the password, so the account can sign in straight away (no activation email)
        if (string.IsNullOrEmpty(password))
            throw new BadRequestException("Password is required");

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            PhoneNumber = phoneNumber,
            Role = normalizedRole,
            EmailConfirmed = true,
            IsActive = true
        };

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded) throw new BadRequestException(result.Errors.FirstOrDefault()?.Description);

        string employeeCode = null;
        if (pmsContext != null)
        {
            try
            {
                employeeCode = await GenerateEmployeeCodeWithRetryAsync(normalizedRole);

                var employee = new Employee
                {
                    UserId = user.Id,
                    EmployeeCode = employeeCode,
                    FullName = !string.IsNullOrWhiteSpace(fullName) ? fullName.Trim() : (user.UserName ?? normalizedRole),
                    Email = user.Email,
                    Phone = user.PhoneNumber,
                    Position = normalizedRole,
                    Status = "Active",
                    CreatedAt = DateTime.UtcNow
                };

                pmsContext.Employees.Add(employee);
                await pmsContext.SaveChangesAsync();
            }
            catch
            {
                await userManager.DeleteAsync(user);
                throw;
            }
        }

        return new CreateUserResponseModel
        {
            Id = Guid.Parse(user.Id),
            EmployeeCode = employeeCode
        };
    }

    private async Task<string> GenerateEmployeeCodeWithRetryAsync(string role)
    {
        var prefix = string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase) ? "QL" : "NV";
        const int maxAttempts = 10;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var suffix = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            var code = $"{prefix}{suffix}";

            var exists = await pmsContext.Employees.AnyAsync(e => e.EmployeeCode == code);
            if (!exists)
            {
                return code;
            }
        }

        throw new BadRequestException($"Unable to generate a unique employee code with prefix '{prefix}' after {maxAttempts} attempts. Please try again.");
    }

    public async Task<LoginResponseModel> LoginAsync(LoginUserModel loginUserModel)
    {
        // Internal accounts sign in with a username, customers with an email; accept either
        var username = loginUserModel.Username?.Trim();
        var email = loginUserModel.Email?.Trim();

        if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(email))
            throw new BadRequestException("Email or password is incorrect");

        var user = await userManager.Users.FirstOrDefaultAsync(u =>
            (username != null && (u.UserName == username || u.Email == username)) ||
            (email != null && u.Email == email));

        // Same status as a wrong password so the response doesn't reveal which emails are registered
        if (user == null)
            throw new BadRequestException("Email or password is incorrect");

        if (!user.IsActive)
            throw new BadRequestException("Account is deactivated. Please contact an administrator.");

        var signInResult = await signInManager.PasswordSignInAsync(user, loginUserModel.Password, false, false);

        if (!signInResult.Succeeded && !signInResult.RequiresTwoFactor)
            throw new BadRequestException("Email or password is incorrect");

        var requiresTwoFactor = user.TwoFactorEnabled || signInResult.RequiresTwoFactor;

        if (requiresTwoFactor)
        {
            if (pmsContext == null || emailService == null)
            {
                throw new InvalidOperationException("Two-factor authentication infrastructure is not available.");
            }

            // Void any pending unconsumed 2FA OTPs for this user
            var pendingOtps = await pmsContext.OtpCodes
                .Where(o => o.UserId == user.Id && o.Purpose == "TwoFactorLogin" && o.ConsumedAt == null)
                .ToListAsync();

            foreach (var p in pendingOtps)
            {
                p.ConsumedAt = DateTime.UtcNow;
            }

            var secretKey = configuration.GetValue<string>("JwtConfiguration:SecretKey");
            var code = TwoFactorTokenHelper.GenerateNumericOtp(6);
            var codeHash = TwoFactorTokenHelper.HashOtpCode(code, user.Email, secretKey);

            var otpRecord = new OtpCode
            {
                UserId = user.Id,
                Destination = user.Email,
                Channel = "Email",
                Purpose = "TwoFactorLogin",
                CodeHash = codeHash,
                AttemptCount = 0,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ConsumedAt = null,
                CreatedAt = DateTime.UtcNow
            };

            pmsContext.OtpCodes.Add(otpRecord);
            await pmsContext.SaveChangesAsync();

            var emailSubject = "Parking Management System - Your Verification Code";
            var emailBody = $"Your verification code is: <strong>{code}</strong>.<br/>This code will expire in 5 minutes.";
            var emailMessage = EmailMessage.Create(user.Email, emailBody, emailSubject);

            try
            {
                await emailService.SendEmailAsync(emailMessage);
            }
            catch
            {
                otpRecord.ConsumedAt = DateTime.UtcNow;
                await pmsContext.SaveChangesAsync();
                throw new BadRequestException("Failed to send verification code email. Please check your email configuration or try again.");
            }

            var challengeToken = TwoFactorTokenHelper.GenerateTwoFactorToken(user.Id, otpRecord.Id, user.Email, configuration);

            return new LoginResponseModel
            {
                UserId = user.Id,
                Username = user.UserName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                Role = user.Role,
                Token = null,
                RequiresTwoFactor = true,
                TwoFactorToken = challengeToken,
                Destination = TwoFactorTokenHelper.MaskEmail(user.Email)
            };
        }

        return BuildLoginResponse(user);
    }

    public async Task RequestLoginOtpAsync(RequestLoginOtpModel model)
    {
        var email = model.Email.Trim();
        // No sign-up step: a new email gets a customer account, confirmed once the code is used
        var user = await userManager.FindByEmailAsync(email) ?? await CreatePasswordlessCustomerAsync(email);

        // Locked-out, deactivated and internal accounts get the same response but no code.
        // Manager/Staff/Admin accounts must sign in with their password (and second factor).
        if (await userManager.IsLockedOutAsync(user) || !user.IsActive || IsInternalRole(user.Role))
            return;

        var code = await userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, LoginOtpPurpose);

        var body = $"""
            <p>Hello {WebUtility.HtmlEncode(user.UserName)},</p>
            <p>Your XXIV Parking login code is:</p>
            <p style="font-size:28px;font-weight:bold;letter-spacing:6px">{code}</p>
            <p>The code is valid for a few minutes and can be used only once.
            If you did not request it, you can ignore this email.</p>
            """;

        await emailService.SendEmailAsync(EmailMessage.Create(user.Email, body, "Your login code"));
    }

    public async Task<LoginResponseModel> LoginWithOtpAsync(VerifyLoginOtpModel model)
    {
        var user = await userManager.FindByEmailAsync(model.Email.Trim());

        if (user == null || IsInternalRole(user.Role))
            throw new BadRequestException(InvalidLoginOtpMessage);

        if (await userManager.IsLockedOutAsync(user))
            throw new BadRequestException("Too many failed attempts. Please try again in a few minutes.");

        if (!user.IsActive)
            throw new BadRequestException("Account is deactivated. Please contact an administrator.");

        var isValid = await userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, LoginOtpPurpose, model.Code.Trim());

        if (!isValid)
        {
            await userManager.AccessFailedAsync(user);
            throw new BadRequestException(InvalidLoginOtpMessage);
        }

        await userManager.ResetAccessFailedCountAsync(user);

        // Using the code proves the mailbox belongs to the user
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }

        // Codes are derived from the security stamp, so rotating it makes this code single-use
        await userManager.UpdateSecurityStampAsync(user);

        return BuildLoginResponse(user);
    }

    public async Task<LoginResponseModel> LoginWithGoogleAsync(GoogleLoginModel model)
    {
        if (googleTokenValidator == null)
            throw new InvalidOperationException("Google sign-in is not available.");

        var google = await googleTokenValidator.ValidateAsync(model.IdToken);

        if (string.IsNullOrWhiteSpace(google.Email) || !google.EmailVerified)
            throw new BadRequestException("Your Google account has no verified email address.");

        // No sign-up step: the first Google sign-in creates the customer account
        var user = await userManager.FindByEmailAsync(google.Email)
                   ?? await CreatePasswordlessCustomerAsync(google.Email, google.Name, emailConfirmed: true);

        // Internal accounts keep their password + second-factor login
        if (IsInternalRole(user.Role))
            throw new BadRequestException("Staff and administrator accounts must sign in with their username and password.");

        if (!user.IsActive)
            throw new BadRequestException("Account is deactivated. Please contact an administrator.");

        if (await userManager.IsLockedOutAsync(user))
            throw new BadRequestException("Too many failed attempts. Please try again in a few minutes.");

        // Google has verified the address
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }

        return BuildLoginResponse(user);
    }

    private static bool IsInternalRole(string role) => role is "Admin" or "Manager" or "Staff";


    /// <summary>Creates a password-less customer account for an email that has never signed in.</summary>
    private async Task<ApplicationUser> CreatePasswordlessCustomerAsync(string email, string fullName = null, bool emailConfirmed = false)
    {
        var user = new ApplicationUser
        {
            UserName = await UniqueUserNameAsync(email),
            Email = email,
            EmailConfirmed = emailConfirmed,
            Role = "User"
        };

        var result = await userManager.CreateAsync(user);
        if (!result.Succeeded) throw new BadRequestException(result.Errors.FirstOrDefault()?.Description);

        if (pmsContext != null)
        {
            try
            {
                pmsContext.Customers.Add(new Customer
                {
                    UserId = user.Id,
                    CustomerCode = $"CUS-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                    FullName = !string.IsNullOrWhiteSpace(fullName) ? fullName.Trim() : user.UserName,
                    Email = user.Email,
                    CustomerType = "Individual",
                    MembershipTier = "Standard",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                await pmsContext.SaveChangesAsync();
            }
            catch
            {
                await userManager.DeleteAsync(user);
                throw;
            }
        }

        return user;
    }

    /// <summary>"an.nguyen@gmail.com" -> "an.nguyen", then "an.nguyen2"… if the name is taken.</summary>
    private async Task<string> UniqueUserNameAsync(string email)
    {
        var baseName = new string(email.Split('@')[0].Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        if (string.IsNullOrEmpty(baseName)) baseName = "user";

        var userName = baseName;
        for (var n = 2; await userManager.FindByNameAsync(userName) != null; n++)
            userName = $"{baseName}{n}";

        return userName;
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
            Token = JwtHelper.GenerateToken(user, configuration),
            RequiresTwoFactor = false,
            TwoFactorToken = null,
            Destination = null
        };
    }

    public async Task<LoginResponseModel> VerifyTwoFactorAsync(TwoFactorLoginModel model)
    {
        if (pmsContext == null)
            throw new InvalidOperationException("Database context is not available.");

        if (string.IsNullOrWhiteSpace(model.Code))
            throw new BadRequestException("Verification code is required.");

        var secretKey = configuration.GetValue<string>("JwtConfiguration:SecretKey");
        OtpCode otpRecord = null;
        string targetUserId = null;

        if (!string.IsNullOrWhiteSpace(model.TwoFactorToken))
        {
            var (isValid, userId, otpId, _, error) = TwoFactorTokenHelper.ValidateTwoFactorToken(model.TwoFactorToken, configuration);
            if (!isValid)
                throw new BadRequestException(error ?? "Invalid verification challenge token.");

            targetUserId = userId;
            otpRecord = await pmsContext.OtpCodes.FirstOrDefaultAsync(o => o.Id == otpId && o.UserId == userId);
            if (otpRecord == null)
                throw new BadRequestException("Verification challenge not found.");
        }
        else if (!string.IsNullOrWhiteSpace(model.Email))
        {
            var user = await userManager.Users.FirstOrDefaultAsync(u => u.Email == model.Email || u.UserName == model.Email);
            if (user == null)
                throw new BadRequestException("Invalid verification request.");

            // Internal accounts must authenticate with username and password first. Passwordless OTP is restricted to customer accounts.
            if (user.Role == "Admin" || user.Role == "Manager" || user.Role == "Staff")
                throw new BadRequestException("Internal staff and administrator accounts must authenticate with username and password first.");

            targetUserId = user.Id;
            otpRecord = await pmsContext.OtpCodes
                .Where(o => (o.UserId == user.Id || o.Destination == user.Email) && (o.Purpose == "TwoFactorLogin" || o.Purpose == "LoginOtp"))
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync();

            if (otpRecord == null)
                throw new BadRequestException("No active verification code found for this account. Please request a new code.");
        }
        else
        {
            throw new BadRequestException("Either verification challenge token or email must be provided.");
        }

        if (otpRecord.ConsumedAt.HasValue)
            throw new BadRequestException("This verification code has already been used. Please request a new code.");

        if (otpRecord.ExpiresAt < DateTime.UtcNow)
        {
            otpRecord.ConsumedAt = DateTime.UtcNow;
            await pmsContext.SaveChangesAsync();
            throw new BadRequestException("Verification code has expired. Please request a new code.");
        }

        const int maxAttempts = 5;
        if (otpRecord.AttemptCount >= maxAttempts)
        {
            otpRecord.ConsumedAt = DateTime.UtcNow;
            await pmsContext.SaveChangesAsync();
            throw new BadRequestException("Maximum verification attempts exceeded. Please request a new code.");
        }

        var expectedHash = TwoFactorTokenHelper.HashOtpCode(model.Code.Trim(), otpRecord.Destination, secretKey);
        if (otpRecord.CodeHash != expectedHash)
        {
            otpRecord.AttemptCount++;
            if (otpRecord.AttemptCount >= maxAttempts)
            {
                otpRecord.ConsumedAt = DateTime.UtcNow;
                await pmsContext.SaveChangesAsync();
                throw new BadRequestException("Maximum verification attempts exceeded. Please request a new code.");
            }

            await pmsContext.SaveChangesAsync();
            throw new BadRequestException($"Invalid verification code. {maxAttempts - otpRecord.AttemptCount} attempts remaining.");
        }

        otpRecord.ConsumedAt = DateTime.UtcNow;
        await pmsContext.SaveChangesAsync();

        var verifiedUser = await userManager.FindByIdAsync(targetUserId);
        if (verifiedUser == null || !verifiedUser.IsActive)
            throw new BadRequestException("Account is inactive or deactivated. Please contact an administrator.");

        var token = JwtHelper.GenerateToken(verifiedUser, configuration);

        return new LoginResponseModel
        {
            UserId = verifiedUser.Id,
            Username = verifiedUser.UserName,
            Email = verifiedUser.Email,
            PhoneNumber = verifiedUser.PhoneNumber,
            Role = verifiedUser.Role,
            Token = token,
            RequiresTwoFactor = false,
            TwoFactorToken = null,
            Destination = null
        };
    }

    public async Task<ResendTwoFactorResponseModel> ResendTwoFactorOtpAsync(ResendTwoFactorModel model)
    {
        if (pmsContext == null || emailService == null)
            throw new InvalidOperationException("Two-factor authentication infrastructure is not available.");

        string targetUserId = null;
        string destinationEmail = null;

        if (!string.IsNullOrWhiteSpace(model.TwoFactorToken))
        {
            var (isValid, userId, _, destination, error) = TwoFactorTokenHelper.ValidateTwoFactorToken(model.TwoFactorToken, configuration);
            if (!isValid)
                throw new BadRequestException(error ?? "Invalid verification challenge token.");

            targetUserId = userId;
            destinationEmail = destination;
        }
        else if (!string.IsNullOrWhiteSpace(model.Email))
        {
            var user = await userManager.Users.FirstOrDefaultAsync(u => u.Email == model.Email || u.UserName == model.Email);
            if (user == null || !user.IsActive)
            {
                return new ResendTwoFactorResponseModel
                {
                    Succeeded = true,
                    Message = "If the email is registered, a new verification code has been sent."
                };
            }

            // Internal staff/manager/admin accounts MUST authenticate with password first via /api/users/authenticate.
            // Passwordless OTP request is restricted to customer accounts.
            if (user.Role == "Admin" || user.Role == "Manager" || user.Role == "Staff")
            {
                return new ResendTwoFactorResponseModel
                {
                    Succeeded = true,
                    Message = "If the email is registered, a new verification code has been sent."
                };
            }

            targetUserId = user.Id;
            destinationEmail = user.Email;
        }
        else
        {
            throw new BadRequestException("Either verification challenge token or email must be provided.");
        }

        var targetUser = await userManager.FindByIdAsync(targetUserId);
        if (targetUser == null || !targetUser.IsActive)
            throw new BadRequestException("Account is inactive or not found.");

        destinationEmail ??= targetUser.Email;

        var recentOtp = await pmsContext.OtpCodes
            .Where(o => o.Destination == destinationEmail && o.CreatedAt > DateTime.UtcNow.AddSeconds(-60))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();

        if (recentOtp != null)
        {
            var remainingSeconds = (int)Math.Ceiling((recentOtp.CreatedAt.AddSeconds(60) - DateTime.UtcNow).TotalSeconds);
            if (remainingSeconds > 0)
                throw new BadRequestException($"Please wait {remainingSeconds} seconds before requesting a new verification code.");
        }

        var previousOtps = await pmsContext.OtpCodes
            .Where(o => o.Destination == destinationEmail && o.ConsumedAt == null)
            .ToListAsync();

        foreach (var p in previousOtps)
        {
            p.ConsumedAt = DateTime.UtcNow;
        }

        var secretKey = configuration.GetValue<string>("JwtConfiguration:SecretKey");
        var code = TwoFactorTokenHelper.GenerateNumericOtp(6);
        var codeHash = TwoFactorTokenHelper.HashOtpCode(code, destinationEmail, secretKey);

        var newOtp = new OtpCode
        {
            UserId = targetUser.Id,
            Destination = destinationEmail,
            Channel = "Email",
            Purpose = "TwoFactorLogin",
            CodeHash = codeHash,
            AttemptCount = 0,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ConsumedAt = null,
            CreatedAt = DateTime.UtcNow
        };

        pmsContext.OtpCodes.Add(newOtp);
        await pmsContext.SaveChangesAsync();

        var emailSubject = "Parking Management System - Your New Verification Code";
        var emailBody = $"Your new verification code is: <strong>{code}</strong>.<br/>This code will expire in 5 minutes.";
        var emailMessage = EmailMessage.Create(destinationEmail, emailBody, emailSubject);

        try
        {
            await emailService.SendEmailAsync(emailMessage);
        }
        catch
        {
            newOtp.ConsumedAt = DateTime.UtcNow;
            await pmsContext.SaveChangesAsync();
            throw new BadRequestException("Failed to send verification code email. Please try again later.");
        }

        var newChallengeToken = TwoFactorTokenHelper.GenerateTwoFactorToken(targetUser.Id, newOtp.Id, destinationEmail, configuration);

        return new ResendTwoFactorResponseModel
        {
            Succeeded = true,
            TwoFactorToken = newChallengeToken,
            Destination = TwoFactorTokenHelper.MaskEmail(destinationEmail),
            Message = "A new verification code has been sent to your email."
        };
    }

    public async Task<IEnumerable<UserResponseModel>> GetAllAsync(string role = null, bool? isActive = null, string searchTerm = null)
    {
        var query = userManager.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(u => u.Role == role);
        }

        if (isActive.HasValue)
        {
            query = query.Where(u => u.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            var matchingEmployeeUserIds = new List<string>();
            if (pmsContext != null)
            {
                matchingEmployeeUserIds = await pmsContext.Employees
                    .Where(e => (e.FullName != null && e.FullName.Contains(term)) ||
                                (e.EmployeeCode != null && e.EmployeeCode.Contains(term)))
                    .Select(e => e.UserId)
                    .ToListAsync();
            }

            query = query.Where(u =>
                (u.UserName != null && u.UserName.Contains(term)) ||
                (u.Email != null && u.Email.Contains(term)) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(term)) ||
                matchingEmployeeUserIds.Contains(u.Id));
        }

        var users = await query.ToListAsync();
        Dictionary<string, Employee> employeeMap = null;

        if (pmsContext != null)
        {
            var userIds = users.Select(u => u.Id).ToList();
            var employees = await pmsContext.Employees.Where(e => userIds.Contains(e.UserId)).ToListAsync();
            employeeMap = employees.ToDictionary(e => e.UserId);
        }

        return users.Select(u =>
        {
            Employee emp = null;
            employeeMap?.TryGetValue(u.Id, out emp);
            return new UserResponseModel
            {
                Id = u.Id,
                UserName = u.UserName,
                FullName = emp?.FullName,
                Email = u.Email,
                PhoneNumber = u.PhoneNumber,
                Role = u.Role,
                EmployeeCode = emp?.EmployeeCode,
                IsActive = u.IsActive,
                CreatedAt = emp?.CreatedAt
            };
        });
    }

    public async Task<IEnumerable<UserResponseModel>> GetInternalUsersAsync(string role = null, bool? isActive = null, string searchTerm = null)
    {
        var query = userManager.Users.Where(u => u.Role == "Manager" || u.Role == "Staff");

        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(u => u.Role == role);
        }

        if (isActive.HasValue)
        {
            query = query.Where(u => u.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            var matchingEmployeeUserIds = new List<string>();
            if (pmsContext != null)
            {
                matchingEmployeeUserIds = await pmsContext.Employees
                    .Where(e => (e.FullName != null && e.FullName.Contains(term)) ||
                                (e.EmployeeCode != null && e.EmployeeCode.Contains(term)))
                    .Select(e => e.UserId)
                    .ToListAsync();
            }

            query = query.Where(u =>
                (u.UserName != null && u.UserName.Contains(term)) ||
                (u.Email != null && u.Email.Contains(term)) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(term)) ||
                matchingEmployeeUserIds.Contains(u.Id));
        }

        var users = await query.ToListAsync();
        Dictionary<string, Employee> employeeMap = null;

        if (pmsContext != null)
        {
            var userIds = users.Select(u => u.Id).ToList();
            var employees = await pmsContext.Employees.Where(e => userIds.Contains(e.UserId)).ToListAsync();
            employeeMap = employees.ToDictionary(e => e.UserId);
        }

        return users.Select(u =>
        {
            Employee emp = null;
            employeeMap?.TryGetValue(u.Id, out emp);
            return new UserResponseModel
            {
                Id = u.Id,
                UserName = u.UserName,
                FullName = emp?.FullName,
                Email = u.Email,
                PhoneNumber = u.PhoneNumber,
                Role = u.Role,
                EmployeeCode = emp?.EmployeeCode,
                IsActive = u.IsActive,
                CreatedAt = emp?.CreatedAt
            };
        });
    }

    public async Task<UserResponseModel> GetByIdAsync(string id)
    {
        var user = await userManager.FindByIdAsync(id);

        if (user == null)
            throw new NotFoundException("User does not exist");

        string employeeCode = null;
        string fullName = null;
        DateTime? createdAt = null;
        if (pmsContext != null)
        {
            var emp = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == user.Id);
            if (emp != null)
            {
                employeeCode = emp.EmployeeCode;
                fullName = emp.FullName;
                createdAt = emp.CreatedAt;
            }
        }

        return new UserResponseModel
        {
            Id = user.Id,
            UserName = user.UserName,
            FullName = fullName,
            Email = user.Email,
            PhoneNumber = user.PhoneNumber,
            Role = user.Role,
            EmployeeCode = employeeCode,
            IsActive = user.IsActive,
            CreatedAt = createdAt
        };
    }

    public async Task<bool> UpdateUserStatusAsync(string currentUserId, string targetUserId, bool isActive)
    {
        if (string.Equals(currentUserId, targetUserId, StringComparison.OrdinalIgnoreCase) && !isActive)
        {
            throw new BadRequestException("Administrators cannot deactivate their own account.");
        }

        var targetUser = await userManager.FindByIdAsync(targetUserId);
        if (targetUser == null)
        {
            throw new NotFoundException("User not found.");
        }

        if (string.Equals(targetUser.Role, "Admin", StringComparison.OrdinalIgnoreCase) && !isActive)
        {
            throw new BadRequestException("Cannot deactivate an Administrator account.");
        }

        targetUser.IsActive = isActive;
        // Accounts created before activation was dropped can be unblocked from the admin list
        if (isActive) targetUser.EmailConfirmed = true;
        var updateResult = await userManager.UpdateAsync(targetUser);
        if (!updateResult.Succeeded)
        {
            throw new BadRequestException(updateResult.Errors.FirstOrDefault()?.Description ?? "Failed to update user status.");
        }

        if (pmsContext != null)
        {
            var customer = await pmsContext.Customers.FirstOrDefaultAsync(c => c.UserId == targetUserId);
            if (customer != null)
            {
                customer.IsActive = isActive;
                customer.UpdatedAt = DateTime.UtcNow;
            }

            var employee = await pmsContext.Employees.FirstOrDefaultAsync(e => e.UserId == targetUserId);
            if (employee != null)
            {
                employee.Status = isActive ? "Active" : "Inactive";
                employee.UpdatedAt = DateTime.UtcNow;
            }

            await pmsContext.SaveChangesAsync();
        }

        return true;
    }
}
