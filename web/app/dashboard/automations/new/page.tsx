import type { Metadata } from 'next';
import AutomationWizard from '@/components/AutomationWizard';

export const metadata: Metadata = { title: 'اتوماسیون جدید' };

export default function NewAutomation() {
  return <AutomationWizard />;
}
