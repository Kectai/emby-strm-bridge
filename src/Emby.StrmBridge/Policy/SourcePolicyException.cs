using System;

namespace Emby.StrmBridge.Policy;

public enum SourceRejectionReason
{
    NotLocalStrm,
    MissingFile,
    FileAccessFailure,
    SymbolicLink,
    FileTooLarge,
    FileChanged,
    InvalidEncoding,
    InvalidRecordCount,
    InvalidUrl,
    UnsafeUrl,
}

public sealed class SourcePolicyException : Exception
{
    public SourcePolicyException(SourceRejectionReason reason)
        : base("The STRM source did not satisfy the configured safety policy.")
    {
        Reason = reason;
    }

    public SourceRejectionReason Reason { get; }
}
