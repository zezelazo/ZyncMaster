import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { TokenStore } from './token-store';

// Adds the identity bearer to outgoing requests. The anonymous identity endpoints
// (magic-link, redeem, refresh) tolerate the header, so no exclusion list is needed.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const access = inject(TokenStore).access();
  if (!access) return next(req);
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${access}` } }));
};
