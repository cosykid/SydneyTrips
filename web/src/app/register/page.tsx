import type { Metadata } from "next";
import { RegisterForm } from "@/components/auth/RegisterForm";

export const metadata: Metadata = {
  title: "Create account · SydneyTrips",
};

export default function RegisterPage(): React.JSX.Element {
  return (
    <div className="bg-muted/40 flex min-h-screen items-center justify-center px-4">
      <div className="w-full max-w-sm space-y-6 rounded-xl border bg-card p-8 shadow-sm">
        <div className="space-y-1.5">
          <h1 className="text-2xl font-semibold tracking-tight">Create account</h1>
          <p className="text-muted-foreground text-sm">
            All you need is an email to start coordinating trips.
          </p>
        </div>
        <RegisterForm />
      </div>
    </div>
  );
}
