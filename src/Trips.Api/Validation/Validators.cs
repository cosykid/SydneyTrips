using FluentValidation;
using Trips.Core.Contracts;

namespace Trips.Api.Validation;

public sealed class CreateTripRequestValidator : AbstractValidator<CreateTripRequest>
{
    public CreateTripRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DestinationName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DestinationLongitude).InclusiveBetween(-180, 180);
        RuleFor(x => x.DestinationLatitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.ArrivalWindowEarliest)
            .LessThanOrEqualTo(x => x.ArrivalWindowLatest)
            .WithMessage("Arrival-window earliest must be on or before arrival-window latest.");
    }
}

public sealed class AddParticipantRequestValidator : AbstractValidator<AddParticipantRequest>
{
    public AddParticipantRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);

        RuleFor(x => x.HomeLongitude)
            .InclusiveBetween(-180, 180)
            .When(x => x.HomeLongitude.HasValue);
        RuleFor(x => x.HomeLatitude)
            .InclusiveBetween(-90, 90)
            .When(x => x.HomeLatitude.HasValue);

        // Either both coords or an address is required.
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.HomeAddress) || (x.HomeLongitude.HasValue && x.HomeLatitude.HasValue))
            .WithName(nameof(AddParticipantRequest.HomeAddress))
            .WithMessage("Either HomeAddress or both HomeLongitude and HomeLatitude must be supplied.");

        RuleFor(x => x.Seats).GreaterThanOrEqualTo(0);
        RuleFor(x => x)
            .Must(x => !x.HasCar || x.Seats > 0)
            .WithName(nameof(AddParticipantRequest.Seats))
            .WithMessage("A driver must have at least one seat.");

        When(x => x.Preferences is not null, () =>
        {
            RuleFor(x => x.Preferences!.WalkBudgetMins).GreaterThanOrEqualTo(0).LessThanOrEqualTo(120);
            RuleFor(x => x.Preferences!.DetourToleranceMins).GreaterThanOrEqualTo(0).LessThanOrEqualTo(120);
            RuleFor(x => x.Preferences!.FairnessWeight).GreaterThanOrEqualTo(0).LessThanOrEqualTo(10);
        });
    }
}

public sealed class PreferencesDtoValidator : AbstractValidator<PreferencesDto>
{
    public PreferencesDtoValidator()
    {
        RuleFor(x => x.WalkBudgetMins).GreaterThanOrEqualTo(0).LessThanOrEqualTo(120);
        RuleFor(x => x.DetourToleranceMins).GreaterThanOrEqualTo(0).LessThanOrEqualTo(120);
        RuleFor(x => x.FairnessWeight).GreaterThanOrEqualTo(0).LessThanOrEqualTo(10);
    }
}

public sealed class OptimiseRequestValidator : AbstractValidator<OptimiseRequest>
{
    public OptimiseRequestValidator()
    {
        RuleFor(x => x.Weights).NotNull();
        When(x => x.Weights is not null, () =>
        {
            RuleFor(x => x.Weights.DriveTime).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.StopCount).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.WalkAndPt).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.ArrivalSpread).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Weights.Fairness).GreaterThanOrEqualTo(0);
        });
    }
}

public sealed class LockSolutionRequestValidator : AbstractValidator<LockSolutionRequest>
{
    public LockSolutionRequestValidator()
    {
        RuleFor(x => x.RunId).NotEqual(Guid.Empty);
        RuleFor(x => x.ParetoIndex).GreaterThanOrEqualTo(0);
    }
}

public sealed class WhatIfRequestValidator : AbstractValidator<WhatIfRequest>
{
    public WhatIfRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => (x.DropParticipantIds?.Count ?? 0) + (x.AddParticipants?.Count ?? 0) > 0 || x.NewWeights is not null)
            .WithMessage("A what-if request must drop participants, add participants, or change weights.");
    }
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
