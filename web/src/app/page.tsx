import { redirect } from "next/navigation";

export default function Home(): never {
  // No login flow — the anonymous-session cookie is stamped on first API call.
  // Send people straight to their trip list.
  redirect("/trips");
}
