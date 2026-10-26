import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { tap } from 'rxjs';
import { DemoToken, Role } from './models';

const EMPLOYEE_ID = '11111111-1111-1111-1111-111111111111';
const MANAGER_ID = '22222222-2222-2222-2222-222222222222';
const MANAGER_TWO_ID = '33333333-3333-3333-3333-333333333333';

@Injectable({ providedIn: 'root' })
export class AuthService {
  token = signal<DemoToken | null>(null);
  constructor(private http: HttpClient) {}

  login(role: Role, secondManager = false) {
    const userId = role === 'Employee' ? EMPLOYEE_ID : (secondManager ? MANAGER_TWO_ID : MANAGER_ID);
    const displayName = role === 'Employee' ? 'Employee One' : (secondManager ? 'Manager Two' : 'Manager One');
    return this.http.post<DemoToken>('http://localhost:5080/api/demo/token',
      { userId, displayName, role }).pipe(tap(t => this.token.set(t)));
  }

  logout() { this.token.set(null); }
}
