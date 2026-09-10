using GenAIPlatform.Application.Core.Errors;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// The Application-level failure contract of <see cref="IRetrievalBaselineCorpusStore" />.
/// Store adapters normalize persistence exceptions into this type at the port boundary and
/// must not surface connection strings, credentials or corpus text in the message.
/// </summary>
public sealed class RetrievalBaselineStoreException(
    string provider,
    string message,
    string? errorCode = null,
    Exception? innerException = null)
    : ProviderException(provider, message, errorCode, statusCode: null, providerErrorCode: null, innerException);
