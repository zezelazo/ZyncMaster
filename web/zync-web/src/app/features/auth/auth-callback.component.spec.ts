import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { AuthCallbackComponent } from './auth-callback.component';

describe('AuthCallbackComponent', () => {
  let redeemed: Array<{ handle: string; nonce: string }>;
  let redeemResult: () => Promise<boolean>;
  let navigated: string[];

  const setup = async (query: Record<string, string>) => {
    redeemed = [];
    navigated = [];
    const auth = {
      redeemHandle: (handle: string, nonce: string): Promise<boolean> => {
        redeemed.push({ handle, nonce });
        return redeemResult();
      },
    };
    const router = {
      navigateByUrl: (url: string): Promise<boolean> => {
        navigated.push(url);
        return Promise.resolve(true);
      },
    };
    await TestBed.configureTestingModule({
      imports: [AuthCallbackComponent],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      ],
    }).compileComponents();
    return TestBed.createComponent(AuthCallbackComponent).componentInstance;
  };

  it('reports an invalid link when the handle is missing', async () => {
    const component = await setup({ nonce: 'n-1' });
    await component.ngOnInit();
    expect(component.error()).toBe('This sign-in link is invalid.');
    expect(redeemed).toEqual([]);
  });

  it('redeems the handle and forwards to the calendar', async () => {
    redeemResult = () => Promise.resolve(true);
    const component = await setup({ handle: 'h-1', nonce: 'n-1' });
    await component.ngOnInit();
    expect(redeemed).toEqual([{ handle: 'h-1', nonce: 'n-1' }]);
    expect(navigated).toEqual(['/calendar']);
    expect(component.error()).toBeNull();
  });

  it('flags a nonce mismatch as a foreign browser session', async () => {
    redeemResult = () => Promise.resolve(false);
    const component = await setup({ handle: 'h-1', nonce: 'WRONG' });
    await component.ngOnInit();
    expect(navigated).toEqual([]);
    expect(component.error()).toBe('This link belongs to another browser session. Request a new one.');
  });

  it('flags a server rejection as an expired or used link', async () => {
    redeemResult = () => Promise.reject(new Error('410'));
    const component = await setup({ handle: 'h-1', nonce: 'n-1' });
    await component.ngOnInit();
    expect(navigated).toEqual([]);
    expect(component.error()).toBe('This sign-in link expired or was already used. Request a new one.');
  });
});
