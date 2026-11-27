'use client';

import { useParams } from 'next/navigation';
import AutomationWizard from '@/components/AutomationWizard';

export default function EditAutomation() {
  const { id } = useParams<{ id: string }>();
  return <AutomationWizard id={id} />;
}
