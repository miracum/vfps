using Vfps.Data.Models;

namespace Vfps.Data
{
    /// <summary>
    /// Store and retrieve pseudonyms.
    /// </summary>
    public interface IPseudonymRepository
    {
        /// <summary>
        /// Store the given pseudonym iff one with the same namespace and original value doesn't already exist.
        /// </summary>
        /// <param name="pseudonym">The pseudonym to store</param>
        /// <returns>The newly stored pseudonym or the one fetched from the store if it already existed or null in case of an error.</returns>
        Task<Pseudonym?> CreateIfNotExist(Pseudonym pseudonym);
    }
}
