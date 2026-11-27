export type TriggerType = 'Comment' | 'Dm';
export type MatchType = 'Exact' | 'Contains';
export type MessageType = 'Text' | 'Image' | 'Button';
export type Role = 'Owner' | 'Admin' | 'Viewer';

export interface ResponseDto { messageType: MessageType; content: string; requireFollow: boolean }

export interface AutomationDto {
  accountId: string;
  triggerType: TriggerType;
  keywords: string;
  matchType: MatchType;
  priority: number;
  followUpAfterHours: number | null;
  followUpText: string | null;
  isActive: boolean;
  responses: ResponseDto[];
}
export interface Automation extends AutomationDto { id: string }

export interface Account { id: string; username: string; isActive: boolean; expiresAt: string | null }
export interface Plan {
  id: string; name: string; priceRial: number; priceToman: number;
  durationDays: number; maxInstagramAccounts: number; maxAutomations: number;
}
export interface Subscription {
  status: 'Trial' | 'Active' | 'Expired' | 'Cancelled';
  plan: string | null; expireDate: string | null; daysLeft: number | null;
}
export interface Invoice { id: string; amountRial: number; status: 'Pending' | 'Paid' | 'Failed'; createdAt: string; planId: string }
export interface LogItem { id: number; accountId: string; eventType: string; status: string; errorMessage: string | null; createdAt: string }
export interface Member { userId: string; phone: string; role: Role; isOwner: boolean; createdAt: string }
export interface PendingInvite { id: string; phone: string; role: Role; expiresAt: string }
export interface MyInvite { id: string; tenant: string; role: Role; expiresAt: string }
export interface TenantRow { tenantId: string; name: string; role: Role; isOwner: boolean; current: boolean }

export interface ButtonContent { message: string; buttons: { type: 'web_url'; title: string; url: string }[] }
