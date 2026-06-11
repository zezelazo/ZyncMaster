import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';
import { TokenStore } from './token-store';

// Signed in -> pass. Otherwise try ONE silent refresh (page reloads drop the in-memory access
// token but keep the persisted refresh token); only then bounce to /login.
export const authGuard: CanActivateFn = async () => {
  const tokens = inject(TokenStore);
  const auth = inject(AuthService);
  const router = inject(Router);
  if (tokens.signedIn) return true;
  if (await auth.refresh()) return true;
  return router.createUrlTree(['/login']);
};
