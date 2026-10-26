import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { EMPTY, Subject, catchError, exhaustMap, finalize, takeUntil } from 'rxjs';
import { AuthService } from './auth.service';
import { ApiService } from './api.service';
import { Role, RequestItem } from './models';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  template: `
  <main class="shell">
    <header class="top">
      <div><div class="eyebrow">DEMO APPLICATION</div><h1>Simple Approval Request System</h1></div>
      <div class="login">
        <label>Demo identity</label>
        <select #identity>
          <option value="Employee">Employee</option>
          <option value="Manager">Manager One</option>
          <option value="Manager2">Manager Two</option>
        </select>
        <button class="secondary" (click)="login(identity.value)">Sign in</button>
        <span class="who" *ngIf="auth.token() as t">{{t.displayName}} · {{t.role}}</span>
      </div>
    </header>

    <div class="notice" *ngIf="message()">{{message()}}</div>
    <div class="error" *ngIf="error()">{{error()}}</div>

    <section class="panel" *ngIf="auth.token() as token">
      <ng-container *ngIf="token.role === 'Employee'; else managerView">
        <h2>Submit a purchase request</h2>
        <p class="muted">No editing after submission. Notifications are simulated as local .eml files.</p>
        <form [formGroup]="form" (ngSubmit)="onSubmit()">
          <label>Title</label><input formControlName="title" maxlength="160" placeholder="e.g. Laptop replacement">
          <label>Description</label><textarea formControlName="description" rows="3" maxlength="4000" placeholder="Why is this purchase needed?"></textarea>
          <label>Amount</label><input type="number" formControlName="amount" min="0.01" step="0.01">
          <button type="submit" [disabled]="busy() || form.invalid">{{busy() ? 'Submitting…' : 'Submit request'}}</button>
        </form>
        <div class="section-heading"><h2>My requests</h2><button class="secondary" (click)="loadMine()">Refresh</button></div>
        <ng-container *ngTemplateOutlet="table; context: {$implicit: mine()}"></ng-container>
        <button class="secondary" *ngIf="mineCursor()" (click)="loadMine(mineCursor())">Load more</button>
      </ng-container>

      <ng-template #managerView>
        <div class="section-heading"><div><h2>Pending approval queue</h2><p class="muted">Open the app as Manager One and Manager Two in separate browser sessions to test concurrency.</p></div>
          <button class="secondary" (click)="loadQueue()">Refresh</button></div>
        <ng-container *ngTemplateOutlet="table; context: {$implicit: queue()}"></ng-container>
        <button class="secondary" *ngIf="queueCursor()" (click)="loadQueue(queueCursor())">Load more</button>
      </ng-template>
    </section>

    <ng-template #table let-items>
      <div class="empty" *ngIf="!items.length">No requests to display.</div>
      <div class="request" *ngFor="let r of items">
        <div class="request-main">
          <div class="request-title">{{r.title}} <span class="status" [class.pending]="r.status==='Pending'">{{r.status}}</span></div>
          <div class="muted">{{r.description}}</div>
          <div class="meta">Amount: {{r.amount | currency}} · Created: {{r.createdUtc | date:'medium'}}</div>
          <div class="meta" *ngIf="r.decidedBy">Decision by {{r.decidedBy}} at {{r.decidedAtUtc | date:'medium'}}</div>
        </div>
        <div class="actions" *ngIf="auth.token()?.role==='Manager' && r.status==='Pending'">
          <input #comment placeholder="Optional comment">
          <button (click)="decide(r,'Approved',comment.value)">Approve</button>
          <button class="danger" (click)="decide(r,'Rejected',comment.value)">Reject</button>
        </div>
      </div>
    </ng-template>

    <footer>Demo only · JWT demo tokens · SQL Server · Transactional outbox · fake email files</footer>
  </main>
  `
})
export class AppComponent {
  form = this.fb.group({
    title: ['', [Validators.required, Validators.maxLength(160)]],
    description: ['', [Validators.required, Validators.maxLength(4000)]],
    amount: [null as number | null, [Validators.required, Validators.min(0.01)]]
  });
  busy = signal(false);
  message = signal('');
  error = signal('');
  mine = signal<RequestItem[]>([]);
  queue = signal<RequestItem[]>([]);
  mineCursor = signal<string | null>(null);
  queueCursor = signal<string | null>(null);
  private submitClicks = new Subject<void>();
  constructor(public auth: AuthService, private api: ApiService, private fb: FormBuilder) {
    this.submitClicks.pipe(
      exhaustMap(() => {
        this.busy.set(true); this.error.set(''); this.message.set('');
        const key = this.idempotencyKey;
        return this.api.create(this.form.getRawValue() as {title: string; description: string; amount: number}, key).pipe(
          catchError((e) => { this.error.set(e.error?.detail ?? e.error?.error ?? 'Submission failed. Retry safely.'); return EMPTY; }),
          finalize(() => this.busy.set(false))
        );
      })
    ).subscribe(result => {
      this.message.set(`Request ${result.id} submitted successfully.`);
      this.idempotencyKey = crypto.randomUUID();
      this.form.reset();
      this.loadMine();
    });
  }
  private idempotencyKey = crypto.randomUUID();

  login(value: string) {
    const role: Role = value === 'Employee' ? 'Employee' : 'Manager';
    const second = value === 'Manager2';
    this.auth.login(role, second).subscribe({
      next: () => { this.message.set('Signed in for demo.'); this.error.set(''); role === 'Employee' ? this.loadMine() : this.loadQueue(); },
      error: () => this.error.set('Could not obtain demo token. Is the API running?')
    });
  }
  onSubmit() { if (this.form.valid && !this.busy()) this.submitClicks.next(); }
  loadMine(cursor?: string | null) {
    this.api.mine(cursor).subscribe({ next: p => {
      this.mine.set(cursor ? [...this.mine(), ...p.items] : p.items); this.mineCursor.set(p.nextCursor ?? null);
    }, error: e => this.error.set(e.error?.detail ?? 'Could not load requests.') });
  }
  loadQueue(cursor?: string | null) {
    this.api.managerQueue(cursor).subscribe({ next: p => {
      this.queue.set(cursor ? [...this.queue(), ...p.items] : p.items); this.queueCursor.set(p.nextCursor ?? null);
    }, error: e => this.error.set(e.error?.detail ?? 'Could not load manager queue.') });
  }
  decide(item: RequestItem, decision: 'Approved' | 'Rejected', comment: string) {
    this.error.set(''); this.message.set('');
    this.api.decide(item, decision, comment).subscribe({
      next: () => { this.message.set(`Request ${decision.toLowerCase()} successfully.`); this.loadQueue(); },
      error: e => {
        if (e.status === 409) {
          this.error.set(`Conflict: another manager may already have decided this request. Queue refreshed.`);
          this.loadQueue();
        } else this.error.set(e.error?.detail ?? e.error?.error ?? 'Decision failed.');
      }
    });
  }
}
