using System.Globalization;
using MatFile.Web.Services;
using MatPlatform;

namespace MatFile.Web.Platform;

/// <summary>
/// MatFiles Seite der Anmeldung über MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): die Person wird über
/// <see cref="UserService.SignInExternalAsync"/> einem Konto zugeordnet (über <c>sub</c>, sonst ein neues Konto; der Claim <c>role</c>, wenn er da ist)
/// und dann mit MatFiles eigener Sitzung angemeldet (<see cref="AuthService.StartSessionAsync"/>), samt ID-Token für die Abmeldung bei MatIdentity.
/// Ein noch nicht verknüpftes Konto mit derselben E-Mail-Adresse (oder demselben Benutzernamen) wird nicht betreten: die Antwort ist
/// <see cref="MatIdentitySignInResult.LinkRequired"/>, die Person meldet sich einmal mit dessen Kennwort an (<c>/Login?local=1&amp;link=1</c>),
/// und die Anmeldeseite verknüpft es (<see cref="UserService.LinkExternalAsync"/>).
/// </summary>
public sealed class MatFileIdentityUsers(UserService users, AuthService auth) : IMatIdentityUsers
{
    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = await users.SignInExternalAsync(
            new ExternalIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role), ct);

        if (result.LinkUserId is { } linkUserId)
        {
            return MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture));
        }

        if (result.UserId is not { } userId)
        {
            return MatIdentitySignInResult.Refused(result.Error ?? "refused");
        }

        await auth.StartSessionAsync(http, userId, person.IdToken, throughIdentity: true);
        return MatIdentitySignInResult.Success();
    }
}
