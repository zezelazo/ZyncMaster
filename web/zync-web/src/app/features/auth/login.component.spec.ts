import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../core/auth/auth.service';
import { LoginComponent } from './login.component';

describe('LoginComponent', () => {
  let requested: string[];
  let fail: boolean;

  beforeEach(async () => {
    requested = [];
    fail = false;
    const auth = {
      requestMagicLink: (email: string): Promise<void> => {
        if (fail) return Promise.reject(new Error('boom'));
        requested.push(email);
        return Promise.resolve();
      },
    };
    await TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [{ provide: AuthService, useValue: auth }],
    }).compileComponents();
  });

  const create = () => TestBed.createComponent(LoginComponent).componentInstance;

  it('rejects a blank email without calling the service', async () => {
    const component = create();
    component.email = '   ';
    await component.send();
    expect(component.error()).toBe('Enter your email address.');
    expect(component.sent()).toBe(false);
    expect(requested).toEqual([]);
  });

  it('sends the trimmed email and flips to the sent state', async () => {
    const component = create();
    component.email = '  z@x.com  ';
    await component.send();
    expect(requested).toEqual(['z@x.com']);
    expect(component.sent()).toBe(true);
    expect(component.error()).toBeNull();
  });

  it('surfaces a retry message when the request fails', async () => {
    fail = true;
    const component = create();
    component.email = 'z@x.com';
    await component.send();
    expect(component.sent()).toBe(false);
    expect(component.error()).toBe('Could not send the sign-in link. Try again.');
  });
});
