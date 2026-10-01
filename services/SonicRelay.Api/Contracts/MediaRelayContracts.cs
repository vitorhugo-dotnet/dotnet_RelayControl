namespace SonicRelay.Api.Contracts;

public sealed record MediaAdmission(Guid AdmissionId, Guid SessionId, Guid ParticipantId, string Grant,
    DateTimeOffset ExpiresAt, string MediaUrl);
public sealed record MediaLease(Guid LeaseId, Guid SessionId, Guid? ParticipantId, string Role, DateTimeOffset ExpiresAt);
